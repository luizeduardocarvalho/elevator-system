using System.Diagnostics;
using System.Globalization;
using System.Text;
using Elevator.Runtime;
using Elevator.Simulation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Elevator.Cli;

/// <summary>`elevator perf --ramp`: the open-loop steady-state throughput ramp (ADR-016), as a markdown results file.</summary>
internal static class PerfRamp
{
    public static async Task<int> Run(CommandContext context, PerfCommand.Settings s)
    {
        var path = ScenarioPaths.Resolve(s.Scenario);
        var loaded = ScenarioLoader.LoadFile(path);
        if (!loaded.IsSuccess)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Invalid scenario {path}[/]");
            return 2;
        }
        var scenario = loaded.Scenario!;
        var rates = ScenarioPaths.List(s.Rates).Select(r => double.Parse(r, CultureInfo.InvariantCulture)).ToList();
        var options = new RampOptions(rates, TimeSpan.FromSeconds(s.StepSeconds), TimeSpan.FromSeconds(s.WarmupSeconds),
            Drain: TimeSpan.FromSeconds(10), BuildingSpeed: s.BuildingSpeed);
        var peak = PeakMoment.Find(scenario);
        var loadAtStart = LoadAverage();

        AnsiConsole.MarkupLineInterpolated($"[grey]Seed: {scenario.Name} at {peak.At}, {peak.Outstanding} outstanding (riders + pickups). Warm-up...[/]");
        await ThroughputRamp.RunStep(scenario, peak.At, 2_000, options with { Step = TimeSpan.FromSeconds(2), Warmup = TimeSpan.FromSeconds(0.5) });

        var runs = new List<IReadOnlyList<RampStep>>();
        for (var r = 1; r <= s.Repeats; r++)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Ramp {r}/{s.Repeats}[/]");
            runs.Add(await ThroughputRamp.Run(scenario, options, step => AnsiConsole.MarkupLineInterpolated(
                $"[grey]  {step.Offered:0}/s: achieved {step.Achieved:0}/s, p99 {step.P99Ms:0.00} ms, in flight {step.InFlightSampled:0.0}, fallback {step.FallbackPct:0}%, lag p99 {step.LagP99Ms:0.000} ms{(step.Saturated ? ", saturated" : "")}[/]")));
        }

        // Per rate, the repeat whose p99 is the median among the repeats that reached it.
        var rows = rates.Select(rate => runs.SelectMany(run => run).Where(step => step.Offered == rate).OrderBy(step => step.P99Ms).ToList())
            .Where(steps => steps.Count > 0)
            .Select(steps => (Step: steps[(steps.Count - 1) / 2], Repeats: steps.Count))
            .ToList();

        Write(context, s, scenario, peak, loadAtStart, rows);
        AnsiConsole.MarkupLineInterpolated($"[grey]Results written to {s.Out}[/]");
        return 0;
    }

    private static void Write(CommandContext context, PerfCommand.Settings s, Scenario scenario, Peak peak, string load,
        List<(RampStep Step, int Repeats)> rows)
    {
        string D(double v, string f = "0.00") => double.IsNaN(v) ? "–" : v.ToString(f, CultureInfo.InvariantCulture);
        var sb = new StringBuilder()
            .AppendLine("# Perf: open-loop steady-state throughput ramp").AppendLine()
            .AppendLine($"- **Command:** `elevator {string.Join(' ', context.Arguments)}`")
            .AppendLine($"- **Machine:** {Environment.ProcessorCount} logical cores, .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; load average at start: {load}")
            .AppendLine($"- **System:** look+{scenario.Dispatch}, {scenario.Elevators} elevators, {scenario.Floors} floors, capacity {scenario.Capacity}, overload fallback to Nearest above {SimEngine.OverloadThreshold} in flight")
            .AppendLine($"- **Seed (steady state):** {scenario.Name} at its busiest moment, {peak.At}: {peak.Outstanding} outstanding " +
                $"({string.Join(", ", peak.PerElevator.Select((e, i) => $"E{i} {e.Riders} riding + {e.Pickups} to pick up"))}). " +
                $"Building {(s.BuildingSpeed == 0 ? "frozen at that moment" : $"at {s.BuildingSpeed}× speed")}; every request retired on acceptance, so the backlog stays constant.")
            .AppendLine($"- **Load:** open loop, one generator thread; request i due at t0 + i/rate. {s.WarmupSeconds:0} s warm-up discarded, {s.StepSeconds:0} s measured per step; " +
                $"{s.Repeats} repeats, each rate reports its median-p99 repeat. The ramp stops after the first step whose achieved rate is below {RampAnalysis.KeepUpFraction:P0} of offered.")
            .AppendLine("- **Latency:** wall clock from the *intended* send time to the accepted assignment (no coordinated omission). " +
                $"A step is **invalid** if the generator's own lag p99 exceeds {RampAnalysis.MaxLagMs:0} ms.")
            .AppendLine($"- **Little's law:** in-flight sampled every ~1 ms vs λ·W (achieved × mean time in system); Δ > {RampAnalysis.LittleTolerancePct:0} % is flagged.")
            .AppendLine()
            .AppendLine("| Offered /s | Achieved /s | p50 ms | p99 ms | p99.9 ms | max ms | In flight (sampled) | λ·W | Little Δ | By fallback | GC 0/1/2 | Lag p99 / max ms | Valid | Repeats |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (st, repeats) in rows)
            sb.AppendLine($"| {D(st.Offered, "0")} | {D(st.Achieved, "0")}{(st.Saturated ? " (saturated)" : "")} | {D(st.P50Ms)} | {D(st.P99Ms)} | {D(st.P999Ms)} | {D(st.MaxMs)} | " +
                $"{D(st.InFlightSampled, "0.0")} | {D(st.LittleL, "0.0")} | {D(st.LittleDeviationPct, "0.0")} %{(st.LittleHolds ? "" : " ⚠")} | {D(st.FallbackPct, "0.0")} % | " +
                $"{st.Gc.Gen0}/{st.Gc.Gen1}/{st.Gc.Gen2} | {D(st.LagP99Ms, "0.000")} / {D(st.LagMaxMs, "0.0")} | {(st.Valid ? "yes" : "**no**")} | {repeats} |");
        if (Path.GetDirectoryName(s.Out) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(s.Out, sb.ToString());
    }

    private static string LoadAverage()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sysctl", "-n vm.loadavg") { RedirectStandardOutput = true });
            var text = p!.StandardOutput.ReadToEnd().Trim().Trim('{', '}').Trim();
            p.WaitForExit();
            return text.Length > 0 ? text : "unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }
}
