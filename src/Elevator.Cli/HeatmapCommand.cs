using System.ComponentModel;
using System.Globalization;
using Elevator.Domain.Events;
using Elevator.Metrics;
using Elevator.Policy;
using Elevator.Policy.Dispatch;
using Elevator.Simulation;
using Elevator.Tui.View;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Elevator.Cli;

internal sealed class HeatmapCommand : Command<HeatmapCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<source>")]
        [Description("A scenario (path, or a name under scenarios/) to run headless, or a JSONL event log (*.jsonl).")]
        public string Source { get; init; } = "";

        [CommandOption("--seed <SEED>")]
        [Description("Override the scenario's seed (scenarios only).")]
        public int? Seed { get; init; }

        [CommandOption("--dispatch <NAME>")]
        [Description("Override the scenario's dispatch policy (scenarios only).")]
        public string? Dispatch { get; init; }

        [CommandOption("--bin <MINUTES>")]
        [Description("Width of a time bin in minutes. Default: 10 (72 columns for a 12-hour day).")]
        public double BinMinutes { get; init; } = 10;

        [CommandOption("--stat <STAT>")]
        [Description("avg (default) or p95.")]
        public string Stat { get; init; } = "avg";

        [CommandOption("--clock-start <HH:MM>")]
        [Description("Time of day at sim time 0, for the hour labels. Default: the scenario's clockStart; elapsed hours for a log.")]
        public string? ClockStart { get; init; }

        [CommandOption("--vs <STRATEGY|LOG>")]
        [Description("Difference map against B, on the same passengers: Δ = this run − B (blue: this run waits less). For a scenario, B is a dispatch policy (such as nearest); for a log, B is another log of the same scenario and seed.")]
        public string? Vs { get; init; }

        [CommandOption("--plain")]
        [Description("Shade characters, no colour. Implied when output is redirected.")]
        public bool Plain { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.BinMinutes <= 0) return Fail("--bin must be > 0");
        if (settings.Stat.ToLowerInvariant() is not ("avg" or "p95")) return Fail($"unknown --stat '{settings.Stat}' (avg or p95)");
        var stat = settings.Stat.Equals("p95", StringComparison.OrdinalIgnoreCase) ? HeatStat.P95 : HeatStat.Avg;

        TimeSpan? clockStart = null;
        if (settings.ClockStart is { } text)
        {
            if (!TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var t)) return Fail($"--clock-start must be HH:MM (got '{text}')");
            clockStart = t;
        }

        IReadOnlyList<DomainEvent> events;
        IReadOnlyList<DomainEvent>? eventsB = null;
        string title;
        string nameA, nameB = "";
        if (settings.Source.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(settings.Source)) return Fail($"no such log: {settings.Source}");
            using var reader = new StreamReader(settings.Source);
            events = JsonlEventLog.Read(reader).ToList();
            var started = events.OfType<SimulationStarted>().FirstOrDefault();
            if (started is null) return Fail($"{settings.Source} has no SimulationStarted event");
            nameA = started.Dispatch ?? "none";
            title = $"{started.Scenario} · {nameA} · seed {started.Seed} · {settings.Source}";
            if (settings.Vs is { } vsLog)
            {
                if (!vsLog.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || !File.Exists(vsLog)) return Fail($"--vs must be another event log when the source is a log (no such log: {vsLog})");
                using var readerB = new StreamReader(vsLog);
                eventsB = JsonlEventLog.Read(readerB).ToList();
                var startedB = eventsB.OfType<SimulationStarted>().FirstOrDefault();
                if (startedB is null) return Fail($"{vsLog} has no SimulationStarted event");
                nameB = startedB.Dispatch ?? "none";
                title = $"{started.Scenario} · seed {started.Seed} · {settings.Source} vs {vsLog}";
            }
        }
        else
        {
            var path = ScenarioPaths.Resolve(settings.Source);
            var loaded = ScenarioLoader.LoadFile(path);
            if (!loaded.IsSuccess)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Invalid scenario {path}:[/]");
                foreach (var error in loaded.Errors) AnsiConsole.MarkupLineInterpolated($"  [red]•[/] {error}");
                return 2;
            }
            var scenario = loaded.Scenario!;
            if (settings.Seed is { } seed) scenario = scenario with { Seed = seed };
            if (settings.Dispatch is { } dispatch)
            {
                if (!DispatchPolicies.IsKnown(dispatch)) return Fail($"unknown dispatch '{dispatch}' (known: {string.Join(", ", DispatchPolicies.Names)})");
                scenario = scenario with { Dispatch = dispatch.ToLowerInvariant() };
            }
            clockStart ??= scenario.ClockStart;
            events = SimEngine.Run(scenario).Events;
            nameA = scenario.Dispatch;
            title = $"{scenario.Name} · {nameA} · seed {scenario.Seed}";
            if (settings.Vs is { } vs)
            {
                var dispatchB = vs.ToLowerInvariant();
                if (!DispatchPolicies.IsKnown(dispatchB))
                    return Fail($"--vs '{vs}' is not a dispatch policy (known: {string.Join(", ", DispatchPolicies.Names)})");
                eventsB = SimEngine.Run(scenario with { Dispatch = dispatchB }).Events;
                nameB = dispatchB;
                title = $"{scenario.Name} · seed {scenario.Seed} · {nameA} vs {nameB}";
            }
        }

        var plain = settings.Plain || Console.IsOutputRedirected;
        var options = new HeatmapOptions(title, clockStart, plain);
        IReadOnlyList<string> lines;
        if (eventsB is null) lines = HeatmapView.Render(WaitHeatmap.FromEvents(events, settings.BinMinutes * 60, stat), options);
        else
        {
            try { lines = HeatmapView.RenderDifference(WaitDiffHeatmap.FromEvents(events, eventsB, settings.BinMinutes * 60, stat), nameA, nameB, options); }
            catch (ArgumentException ex) { return Fail(ex.Message); }
        }
        foreach (var line in lines)
        {
            if (plain) Console.WriteLine(line);
            else AnsiConsole.MarkupLine(line);
        }
        return 0;
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]{message}[/]");
        return 2;
    }
}
