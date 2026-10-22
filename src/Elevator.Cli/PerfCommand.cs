using System.ComponentModel;
using System.Globalization;
using System.Text;
using Elevator.Runtime;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Elevator.Cli;

internal sealed class PerfCommand : AsyncCommand<PerfCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--levels <LIST>")]
        [Description("Concurrent submitter counts. Default: 100,1000,10000")]
        public string Levels { get; init; } = "100,1000,10000";

        [CommandOption("--requests-each <N>")]
        public int RequestsEach { get; init; } = 1;

        [CommandOption("--scheduling <NAME>")]
        public string Scheduling { get; init; } = "look";

        [CommandOption("--dispatch <NAME>")]
        public string Dispatch { get; init; } = "cost";

        [CommandOption("--elevators <N>")]
        public int Elevators { get; init; } = 4;

        [CommandOption("--floors <N>")]
        public int Floors { get; init; } = 20;

        [CommandOption("--capacity <N>")]
        public int Capacity { get; init; } = 10;

        [CommandOption("--soak-seconds <N>")]
        [Description("Wall seconds of sustained load (0 = skip). Default: 60")]
        public int SoakSeconds { get; init; } = 60;

        [CommandOption("--soak-rate <N>")]
        [Description("Submissions per wall second during the soak. Default: 20")]
        public double SoakRate { get; init; } = 20;

        [CommandOption("--soak-speed <N>")]
        [Description("Sim speed during the soak, so the building keeps up with the rate. Default: 100")]
        public double SoakSpeed { get; init; } = 100;

        [CommandOption("--overload-threshold <N>")]
        [Description("Requests in flight before falling back to Nearest; 'none' to disable. Default: 200")]
        public string OverloadThreshold { get; init; } = "200";

        [CommandOption("--log-dir <DIR>")]
        [Description("Write each burst level's event log as JSONL into this directory (for diagnosis).")]
        public string? LogDir { get; init; }

        [CommandOption("--out <PATH>")]
        public string Out { get; init; } = Path.Combine("out", "perf.md");

        [CommandOption("--ramp")]
        [Description("Open-loop steady-state throughput ramp from a scenario's busiest moment (ADR-016) instead of bursts.")]
        public bool Ramp { get; init; }

        [CommandOption("--rates <LIST>")]
        [Description("Ramp: offered requests per second, in order. Default: 1000,2000,5000,10000,15000,20000,30000,40000,50000,60000,70000")]
        public string Rates { get; init; } = "1000,2000,5000,10000,15000,20000,30000,40000,50000,60000,70000";

        [CommandOption("--step-seconds <N>")]
        [Description("Ramp: measured seconds per step. Default: 30")]
        public double StepSeconds { get; init; } = 30;

        [CommandOption("--warmup-seconds <N>")]
        [Description("Ramp: seconds discarded at the start of each step. Default: 5")]
        public double WarmupSeconds { get; init; } = 5;

        [CommandOption("--repeats <N>")]
        [Description("Ramp: repeat the whole ramp; each rate reports its median-p99 repeat. Default: 3")]
        public int Repeats { get; init; } = 3;

        [CommandOption("--building-speed <N>")]
        [Description("Ramp: 0 = building frozen at the seeded moment (steady state, default); 1 = real time.")]
        public double BuildingSpeed { get; init; }

        [CommandOption("--scenario <NAME>")]
        [Description("Ramp: the scenario whose busiest moment seeds every step. Default: lunch")]
        public string Scenario { get; init; } = "lunch";
    }

    public const double LatencySloMs = 100;
    public const double HeapGrowthSloPct = 10;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        if (s.Ramp) return await PerfRamp.Run(context, s).ConfigureAwait(false);
        int? overload = s.OverloadThreshold == "none" ? null : int.Parse(s.OverloadThreshold, CultureInfo.InvariantCulture);
        var options = new RuntimeOptions(s.Floors, s.Elevators, s.Capacity, Scheduling: s.Scheduling, Dispatch: s.Dispatch, Speed: 1,
            OverloadThreshold: overload);
        var levels = ScenarioPaths.List(s.Levels).Select(l => int.Parse(l, CultureInfo.InvariantCulture)).ToList();
        var bursts = new List<BurstResult>();

        AnsiConsole.MarkupLine("[grey]Warm-up...[/]");
        await PerfRunner.Burst(options, 200, 5, TimeSpan.FromSeconds(60), seed: 99);

        foreach (var level in levels)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Burst: {level} concurrent submitters × {s.RequestsEach}...[/]");
            StreamWriter? log = null;
            if (s.LogDir is { } dir)
            {
                Directory.CreateDirectory(dir);
                log = new StreamWriter(Path.Combine(dir, $"burst-{level}.jsonl")) { NewLine = "\n" };
            }
            bursts.Add(await PerfRunner.Burst(options, level, s.RequestsEach, TimeSpan.FromSeconds(120), log: log));
            if (log is not null) await log.DisposeAsync();
        }

        SoakResult? soak = null;
        if (s.SoakSeconds > 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Soak: {s.SoakRate}/s for {s.SoakSeconds} s at {s.SoakSpeed}× sim speed...[/]");
            soak = await PerfRunner.Soak(options with { Speed = s.SoakSpeed }, s.SoakRate, TimeSpan.FromSeconds(s.SoakSeconds), TimeSpan.FromSeconds(Math.Min(10, s.SoakSeconds / 4.0)));
        }

        var latency = new Table().Title($"Assignment latency (wall clock) · {s.Scheduling}+{s.Dispatch} · {s.Elevators} elevators, {s.Floors} floors")
            .AddColumns("Submitters", "Requests", "Assigned", "Lost", "Dup", "p50 ms", "p95 ms", "p99 ms", "max ms", "req/s", "by fallback");
        foreach (var b in bursts)
            latency.AddRow(I(b.Submitters), I(b.Submitted), I(b.Assigned), I(b.Lost), I(b.Duplicated),
                D(b.Latency.P50Ms), D(b.Latency.P95Ms), D(b.Latency.P99Ms), D(b.Latency.MaxMs), D(b.ThroughputPerSecond, "0"),
                $"{b.Decisions.Fallback}/{b.Decisions.Primary + b.Decisions.Fallback}");
        AnsiConsole.Write(latency);

        if (bursts.MaxBy(b => b.Submitted) is { } big)
        {
            var breakdown = new Table().Title($"Pipeline per request ({big.Submitters} submitters)")
                .AddColumns("Actor", "Messages/request", "Handler µs/request", "Delivery µs/request");
            foreach (var st in big.Stats.OrderBy(x => x.Kind))
                breakdown.AddRow(st.Kind.ToString(), D(st.Messages / (double)big.Submitted), D(st.InHandlers.Microseconds / big.Submitted),
                    D(st.Delivering.Microseconds / big.Submitted));
            AnsiConsole.Write(breakdown);
        }

        var slos = new List<(string Slo, string Measured, bool Pass)>();
        foreach (var b in bursts)
            slos.Add(($"Assignment p99 < {LatencySloMs} ms @ {b.Submitters} concurrent", $"{D(b.Latency.P99Ms)} ms", b.Latency.P99Ms < LatencySloMs));
        var hundredPlus = bursts.Where(b => b.Submitters >= 100).ToList();
        slos.Add(("Handles 100+ concurrent requests (all assigned)", string.Join(", ", hundredPlus.Select(b => $"{b.Assigned}/{b.Submitted}")),
            hundredPlus.Count > 0 && hundredPlus.All(b => b.Lost == 0)));
        slos.Add(("Lost assignments = 0", I(bursts.Sum(b => b.Lost) + (soak?.Lost ?? 0)), bursts.All(b => b.Lost == 0) && (soak?.Lost ?? 0) == 0));
        slos.Add(("Duplicated assignments = 0", I(bursts.Sum(b => b.Duplicated) + (soak?.Duplicated ?? 0)), bursts.All(b => b.Duplicated == 0) && (soak?.Duplicated ?? 0) == 0));
        if (soak is not null)
            slos.Add(($"Memory: heap growth < {HeapGrowthSloPct}% over {s.SoakSeconds} s soak",
                $"{D(soak.GrowthPct)}% ({D(soak.HeapMb[0])} → {D(soak.HeapMb[^1])} MB, {soak.Delivered}/{soak.Submitted} delivered)",
                soak.GrowthPct < HeapGrowthSloPct));

        var table = new Table().Title("Spec SLOs").AddColumns("SLO", "Measured", "Result");
        foreach (var (slo, measured, pass) in slos) table.AddRow(Markup.Escape(slo), Markup.Escape(measured), pass ? "[green]PASS[/]" : "[red]FAIL[/]");
        AnsiConsole.Write(table);

        WriteMarkdown(s, context, bursts, soak, slos);
        AnsiConsole.MarkupLineInterpolated($"[grey]Results written to {s.Out}[/]");
        return slos.All(x => x.Pass) ? 0 : 1;
    }

    private static void WriteMarkdown(Settings s, CommandContext context, List<BurstResult> bursts, SoakResult? soak, List<(string Slo, string Measured, bool Pass)> slos)
    {
        var sb = new StringBuilder()
            .AppendLine("# Perf: real clock, real threads").AppendLine()
            .AppendLine($"- **Command:** `elevator {string.Join(' ', context.Arguments)}`")
            .AppendLine($"- **System:** {s.Scheduling}+{s.Dispatch}, {s.Elevators} elevators, {s.Floors} floors, capacity {s.Capacity}")
            .AppendLine($"- **Machine:** {Environment.ProcessorCount} logical cores, .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}")
            .AppendLine("- **Latency:** wall clock from `AssignRequest` to the accepted assignment (queueing in the bounded request channel included).")
            .AppendLine($"- **Overload fallback:** {(s.OverloadThreshold == "none" ? "disabled" : $"Nearest above {s.OverloadThreshold} requests in flight")}")
            .AppendLine().AppendLine("| Submitters | Requests | Assigned | Lost | Dup | p50 ms | p95 ms | p99 ms | max ms | req/s | decided by fallback |").AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var b in bursts)
            sb.AppendLine($"| {b.Submitters} | {b.Submitted} | {b.Assigned} | {b.Lost} | {b.Duplicated} | {D(b.Latency.P50Ms)} | {D(b.Latency.P95Ms)} | {D(b.Latency.P99Ms)} | {D(b.Latency.MaxMs)} | {D(b.ThroughputPerSecond, "0")} | {b.Decisions.Fallback} of {b.Decisions.Primary + b.Decisions.Fallback} |");
        if (bursts.MaxBy(b => b.Submitted) is { } big)
        {
            sb.AppendLine().AppendLine($"Pipeline per request at {big.Submitters} submitters (wall clock):").AppendLine()
              .AppendLine("| Actor | Messages / request | Handler µs / request | Delivery µs / request |").AppendLine("|---|---|---|---|");
            foreach (var st in big.Stats.OrderBy(x => x.Kind))
                sb.AppendLine($"| {st.Kind} | {D(st.Messages / (double)big.Submitted)} | {D(st.InHandlers.Microseconds / big.Submitted)} | {D(st.Delivering.Microseconds / big.Submitted)} |");
        }
        if (soak is not null)
            sb.AppendLine().AppendLine($"Soak: {soak.Submitted} submitted, {soak.Delivered} delivered, heap samples (MB): {string.Join(", ", soak.HeapMb.Select(m => D(m)))}; p99 {D(soak.Latency.P99Ms)} ms.");
        sb.AppendLine().AppendLine("| SLO | Measured | Result |").AppendLine("|---|---|---|");
        foreach (var (slo, measured, pass) in slos) sb.AppendLine($"| {slo} | {measured} | {(pass ? "PASS" : "FAIL")} |");
        if (Path.GetDirectoryName(s.Out) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(s.Out, sb.ToString());
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string D(double v, string format = "0.00") => double.IsNaN(v) ? "–" : v.ToString(format, CultureInfo.InvariantCulture);
}
