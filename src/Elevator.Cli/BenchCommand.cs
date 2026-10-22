using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Elevator.Metrics;
using Elevator.Policy;
using Elevator.Policy.Dispatch;
using Elevator.Simulation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Elevator.Cli;

internal sealed class BenchCommand : Command<BenchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--scenarios <LIST>")]
        [Description("Comma-separated scenario names (scenarios/<name>.yaml) or paths. Default: up-peak,down-peak,lunch")]
        public string Scenarios { get; init; } = "up-peak,down-peak,lunch";

        [CommandOption("--dispatch <LIST>")]
        [Description("Dispatch policies of the grid. Default: all")]
        public string? Dispatch { get; init; }

        [CommandOption("--faults <LIST>")]
        [Description("on, off or on,off: apply the scenarios' fault schedules (ADR-013). on,off adds a paired dimension (\"/no-faults\"). Default: on")]
        public string Faults { get; init; } = "on";

        [CommandOption("--strategies <LIST>")]
        [Description("Explicit strategies (e.g. nearest,cost:1:5); replaces the grid.")]
        public string? Strategies { get; init; }

        [CommandOption("--seeds <N>")]
        public int Seeds { get; init; } = 20;

        [CommandOption("--first-seed <N>")]
        public int FirstSeed { get; init; } = 1;

        [CommandOption("--baseline <STRATEGY>")]
        public string Baseline { get; init; } = "nearest";

        [CommandOption("--out <PATH>")]
        [Description("Markdown results file. Default: out/bench.md")]
        public string Out { get; init; } = Path.Combine("out", "bench.md");

        [CommandOption("--title <TEXT>")]
        public string Title { get; init; } = "Bench results";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        List<Strategy> strategies;
        List<(Strategy Strategy, bool Faults, string Label)> variants;
        try
        {
            strategies = ScenarioPaths.List(settings.Strategies ?? settings.Dispatch ?? string.Join(',', DispatchPolicies.Names))
                .Select(Strategy.Parse).ToList();
            foreach (var s in strategies) DispatchPolicies.Create(s.Dispatch, new LookScheduling(), Domain.ElevatorTiming.Default);

            var faults = ScenarioPaths.List(settings.Faults).Select(f => f switch
            {
                "on" => true,
                "off" => false,
                _ => throw new FormatException($"--faults must be on, off or on,off (got '{f}')"),
            }).Distinct().ToList();
            variants = (from st in strategies
                        from f in faults
                        select (st, f, faults.Count > 1 && !f ? st + "/no-faults" : st.ToString())).ToList();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 2;
        }

        var scenarios = new List<Scenario>();
        foreach (var name in ScenarioPaths.List(settings.Scenarios))
        {
            var loaded = ScenarioLoader.LoadFile(ScenarioPaths.Resolve(name));
            if (!loaded.IsSuccess)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Invalid scenario {name}:[/] {string.Join("; ", loaded.Errors)}");
                return 2;
            }
            scenarios.Add(loaded.Scenario!);
        }

        var seeds = Enumerable.Range(settings.FirstSeed, settings.Seeds).ToList();
        var jobs = (from sc in scenarios from seed in seeds from v in variants select (sc, seed, v)).ToList();
        var results = new ConcurrentBag<BenchRun>();
        var stopwatch = Stopwatch.StartNew();

        AnsiConsole.Progress().Start(ctx =>
        {
            var task = ctx.AddTask($"{jobs.Count} runs ({scenarios.Count} scenarios × {seeds.Count} seeds × {variants.Count} strategies)", maxValue: jobs.Count);
            Parallel.ForEach(jobs, new ParallelOptions { CancellationToken = cancellationToken }, job =>
            {
                var scenario = job.sc with { Seed = job.seed };
                if (!job.v.Faults) scenario = scenario with { Faults = [] };
                var report = ReportBuilder.Build(SimEngine.Run(scenario, job.v.Strategy).Events);
                results.Add(new BenchRun(job.sc.Name, job.v.Label, job.seed, report));
                task.Increment(1);
            });
        });

        var ordered = results
            .OrderBy(r => scenarios.FindIndex(s => s.Name == r.Scenario))
            .ThenBy(r => variants.FindIndex(v => v.Label == r.Strategy))
            .ThenBy(r => r.Seed)
            .ToList();
        var rows = BenchAnalysis.Summarize(ordered, settings.Baseline, lowerBounds: []);

        foreach (var scenario in rows.GroupBy(r => r.Scenario))
        {
            var table = new Table().Title(scenario.Key)
                .AddColumns("Strategy", "Wait avg", "Wait p95", "Journey avg", "Left behind", "Served %", "Δ wait avg", "Δ wait p95");
            foreach (var r in scenario)
                table.AddRow(
                    Markup.Escape(r.Role.Length > 0 ? $"{r.Strategy} ({r.Role})" : r.Strategy),
                    r.WaitAvg.Format(), r.WaitP95.Format(), r.JourneyAvg.Format(), r.LeftBehind.Format(), r.ServedPct.Format(),
                    FormatDelta(r.DeltaWaitAvg), FormatDelta(r.DeltaWaitP95));
            AnsiConsole.Write(table);
        }

        var meta = new Dictionary<string, string>
        {
            ["Command"] = "`elevator " + string.Join(' ', context.Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)) + "`",
            ["Seeds"] = $"{settings.FirstSeed}..{settings.FirstSeed + settings.Seeds - 1} ({settings.Seeds} per strategy)",
            ["Baseline"] = settings.Baseline,
            ["Scenarios"] = string.Join(", ", scenarios.Select(s => $"{s.Name} ({s.Elevators} elevators, {s.Floors} floors, capacity {s.Capacity})")),
            ["Runs"] = $"{jobs.Count}, {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s wall",
        };
        if (Path.GetDirectoryName(settings.Out) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(settings.Out, BenchAnalysis.ToMarkdown(rows, settings.Title, meta));
        AnsiConsole.MarkupLineInterpolated($"[grey]Results written to {settings.Out}[/]");
        return 0;
    }

    private static string FormatDelta(Estimate? e) =>
        e is null ? "–" : Markup.Escape(e.Format()) + (e.ExcludesZero ? (e.Mean < 0 ? " [green]✱[/]" : " [red]✱[/]") : "");
}
