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

/// <summary>
/// Runs a grid of strategies over many seeds and compares them to a baseline with paired confidence intervals.
/// Every run is the deterministic sim, so the whole bench is reproducible; runs execute in parallel.
/// </summary>
internal sealed class BenchCommand : Command<BenchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--scenarios <LIST>")]
        [Description("Comma-separated scenario names (scenarios/<name>.yaml) or paths. Default: up-peak,down-peak,lunch")]
        public string Scenarios { get; init; } = "up-peak,down-peak,lunch";

        [CommandOption("--scheduling <LIST>")]
        [Description("Scheduling policies of the grid. Default: all")]
        public string? Scheduling { get; init; }

        [CommandOption("--dispatch <LIST>")]
        [Description("Dispatch policies of the grid. Default: all")]
        public string? Dispatch { get; init; }

        [CommandOption("--rebalance <LIST>")]
        [Description("off, on or off,on. Default: off")]
        public string Rebalance { get; init; } = "off";

        [CommandOption("--strategies <LIST>")]
        [Description("Explicit strategies (e.g. fifo+nearest,look+cost); replaces the grid.")]
        public string? Strategies { get; init; }

        [CommandOption("--seeds <N>")]
        public int Seeds { get; init; } = 20;

        [CommandOption("--first-seed <N>")]
        public int FirstSeed { get; init; } = 1;

        [CommandOption("--baseline <STRATEGY>")]
        public string Baseline { get; init; } = "fifo+nearest";

        [CommandOption("--out <PATH>")]
        [Description("Markdown results file. Default: out/bench.md")]
        public string Out { get; init; } = Path.Combine("out", "bench.md");

        [CommandOption("--title <TEXT>")]
        public string Title { get; init; } = "Bench results";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        List<Strategy> strategies;
        try
        {
            strategies = settings.Strategies is { } explicitList
                ? ScenarioPaths.List(explicitList).Select(Strategy.Parse).ToList()
                : (from s in ScenarioPaths.List(settings.Scheduling ?? string.Join(',', SchedulingPolicies.Names))
                   from d in ScenarioPaths.List(settings.Dispatch ?? string.Join(',', DispatchPolicies.Names))
                   from r in ScenarioPaths.List(settings.Rebalance)
                   select new Strategy(s.ToLowerInvariant(), d.ToLowerInvariant(), r == "on")).ToList();
            foreach (var s in strategies)
            {
                SchedulingPolicies.Create(s.Scheduling);
                DispatchPolicies.Create(s.Dispatch, SchedulingPolicies.Create(s.Scheduling), Domain.ElevatorTiming.Default);
            }
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
        var jobs = (from sc in scenarios from seed in seeds from st in strategies select (sc, seed, st)).ToList();
        var results = new ConcurrentBag<BenchRun>();
        var stopwatch = Stopwatch.StartNew();

        AnsiConsole.Progress().Start(ctx =>
        {
            var task = ctx.AddTask($"{jobs.Count} runs ({scenarios.Count} scenarios × {seeds.Count} seeds × {strategies.Count} strategies)", maxValue: jobs.Count);
            Parallel.ForEach(jobs, new ParallelOptions { CancellationToken = cancellationToken }, job =>
            {
                var scenario = job.sc with { Seed = job.seed };
                var report = ReportBuilder.Build(SimEngine.Run(scenario, job.st).Events);
                results.Add(new BenchRun(job.sc.Name, job.st.ToString(), job.seed, report));
                task.Increment(1);
            });
        });

        var ordered = results
            .OrderBy(r => scenarios.FindIndex(s => s.Name == r.Scenario))
            .ThenBy(r => strategies.FindIndex(s => s.ToString() == r.Strategy))
            .ThenBy(r => r.Seed)
            .ToList();
        var lowerBounds = strategies.Where(s => s.Dispatch == "roundrobin").Select(s => s.ToString()).ToList();
        var rows = BenchAnalysis.Summarize(ordered, settings.Baseline, lowerBounds);

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
            ["Command"] = "`elevator bench " + string.Join(' ', context.Arguments) + "`",
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
