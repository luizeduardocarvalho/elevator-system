using System.ComponentModel;
using System.Globalization;
using Elevator.Domain.Events;
using Elevator.Metrics;
using Elevator.Policy.Dispatch;
using Elevator.Simulation;
using Elevator.Tui.View;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Elevator.Cli;

/// <summary>"The shape of a working day": wait by floor × time of day, from an event log (or a scenario run headless).</summary>
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
        string title;
        if (settings.Source.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(settings.Source)) return Fail($"no such log: {settings.Source}");
            using var reader = new StreamReader(settings.Source);
            events = JsonlEventLog.Read(reader).ToList();
            var started = events.OfType<SimulationStarted>().FirstOrDefault();
            if (started is null) return Fail($"{settings.Source} has no SimulationStarted event");
            title = $"{started.Scenario} · {started.Scheduling}+{started.Dispatch ?? "none"} · seed {started.Seed} · {settings.Source}";
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
            title = $"{scenario.Name} · {scenario.Scheduling}+{scenario.Dispatch} · seed {scenario.Seed}";
        }

        var map = WaitHeatmap.FromEvents(events, settings.BinMinutes * 60, stat);
        var plain = settings.Plain || Console.IsOutputRedirected;
        foreach (var line in HeatmapView.Render(map, new HeatmapOptions(title, clockStart, plain)))
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
