using System.ComponentModel;
using System.Globalization;
using Elevator.Metrics;
using Elevator.Policy;
using Elevator.Simulation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Elevator.Cli;

internal sealed class RunCommand : Command<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<scenario>")]
        [Description("Path to a YAML scenario file.")]
        public string Scenario { get; init; } = "";

        [CommandOption("--seed <SEED>")]
        [Description("Override the scenario's seed.")]
        public int? Seed { get; init; }

        [CommandOption("--scheduling <NAME>")]
        [Description("Override the scenario's scheduling policy (fifo).")]
        public string? Scheduling { get; init; }

        [CommandOption("--log <PATH>")]
        [Description("Where to write the JSONL event log. Default: out/<scenario>-seed<seed>.jsonl")]
        public string? Log { get; init; }

        [CommandOption("-v|--verbose")]
        [Description("Print every event as it happened.")]
        public bool Verbose { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var loaded = ScenarioLoader.LoadFile(settings.Scenario);
        if (!loaded.IsSuccess)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Invalid scenario {settings.Scenario}:[/]");
            foreach (var error in loaded.Errors) AnsiConsole.MarkupLineInterpolated($"  [red]•[/] {error}");
            return 2;
        }

        var scenario = loaded.Scenario!;
        if (settings.Seed is { } seed) scenario = scenario with { Seed = seed };
        if (settings.Scheduling is { } scheduling)
        {
            if (!SchedulingPolicies.Names.Contains(scheduling, StringComparer.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Unknown scheduling '{scheduling}'[/] (known: {string.Join(", ", SchedulingPolicies.Names)})");
                return 2;
            }
            scenario = scenario with { Scheduling = scheduling.ToLowerInvariant() };
        }

        var result = ScenarioRunner.Run(scenario);

        var logPath = settings.Log ?? Path.Combine("out", $"{scenario.Name}-seed{scenario.Seed}.jsonl");
        JsonlEventLog.WriteFile(logPath, result.Events);

        if (settings.Verbose)
            foreach (var e in result.Events)
                AnsiConsole.WriteLine(EventFormatter.Format(e));

        // The report is built from the log file just written: the log alone must be enough to measure a run.
        using var reader = new StreamReader(logPath);
        var report = ReportBuilder.Build(JsonlEventLog.Read(reader));
        AnsiConsole.Write(Render(report));
        AnsiConsole.MarkupLineInterpolated($"[grey]Event log: {logPath} ({result.Events.Count} events)[/]");
        return report.Unserved == 0 ? 0 : 1;
    }

    private static Table Render(RunReport r)
    {
        static string S(double? seconds) => seconds is { } s ? s.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "–";

        var table = new Table()
            .Title($"{r.Scenario} · {r.Scheduling} · seed {r.Seed}")
            .AddColumn("Metric")
            .AddColumn(new TableColumn("Value").RightAligned());

        table.AddRow("Requests served", $"{r.Delivered} / {r.Passengers}");
        table.AddRow(r.Unserved == 0 ? "Unserved" : "[red]Unserved[/]", r.Unserved.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Rejected requests", r.RejectedRequests.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Wait avg", S(r.Wait.Avg));
        table.AddRow("Wait p95", S(r.Wait.P95));
        table.AddRow("Wait max", S(r.Wait.Max));
        table.AddRow("Ride avg", S(r.Ride.Avg));
        table.AddRow("Ride max", S(r.Ride.Max));
        table.AddRow("Floors travelled", r.FloorsTravelled.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Stops", r.Stops.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Sim time", S(r.SimTimeS));
        return table;
    }
}
