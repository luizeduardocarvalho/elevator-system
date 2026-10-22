using System.ComponentModel;
using System.Globalization;
using Elevator.Metrics;
using Elevator.Policy;
using Elevator.Policy.Dispatch;
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

        [CommandOption("--dispatch <NAME>")]
        [Description("Override the scenario's dispatch policy (cost or nearest).")]
        public string? Dispatch { get; init; }

        [CommandOption("--no-faults")]
        [Description("Ignore the scenario's fault schedule (ADR-013).")]
        public bool NoFaults { get; init; }

        [CommandOption("--passengers <PATH>")]
        [Description("Write one CSV row per passenger: lifecycle timestamps and wait attribution.")]
        public string? Passengers { get; init; }

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
        if (settings.Dispatch is { } dispatch)
        {
            if (!DispatchPolicies.IsKnown(dispatch))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Unknown dispatch '{dispatch}'[/] (known: {string.Join(", ", DispatchPolicies.Names)})");
                return 2;
            }
            scenario = scenario with { Dispatch = dispatch.ToLowerInvariant() };
        }

        if (settings.NoFaults) scenario = scenario with { Faults = [] };
        var result = ScenarioRunner.Run(scenario);

        var logPath = settings.Log ?? Path.Combine("out", $"{scenario.Name}-seed{scenario.Seed}.jsonl");
        JsonlEventLog.WriteFile(logPath, result.Events);

        if (settings.Verbose)
            foreach (var e in result.Events)
                AnsiConsole.WriteLine(EventFormatter.Format(e));

        using var reader = new StreamReader(logPath);
        var report = ReportBuilder.Build(JsonlEventLog.Read(reader));
        AnsiConsole.Write(Render(report));
        if (settings.Passengers is { } csvPath)
        {
            reader.BaseStream.Position = 0;
            reader.DiscardBufferedData();
            PassengerCsv.Write(csvPath, PassengerLifecycle.Analyze(JsonlEventLog.Read(reader)));
            AnsiConsole.MarkupLineInterpolated($"[grey]Passenger lifecycles: {csvPath}[/]");
        }
        AnsiConsole.MarkupLineInterpolated($"[grey]Event log: {logPath} ({result.Events.Count} events)[/]");
        return report.Unserved == 0 ? 0 : 1;
    }

    private static Table Render(RunReport r)
    {
        static string S(double? seconds) => seconds is { } s ? s.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "–";

        var table = new Table()
            .Title($"{r.Scenario} · {r.Scheduling}+{r.Dispatch} · {r.Elevators} elevator(s) · seed {r.Seed}")
            .AddColumn("Metric")
            .AddColumn(new TableColumn("Value").RightAligned());

        table.AddRow("Requests served", $"{r.Delivered} / {r.Passengers}");
        table.AddRow(r.Unserved == 0 ? "Unserved" : "[red]Unserved[/]", r.Unserved.ToString(CultureInfo.InvariantCulture));
        if (r.RefusedByPolicy > 0) table.AddRow("Refused by access policy", r.RefusedByPolicy.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Invalid requests", r.RejectedRequests.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Wait avg", S(r.Wait.Avg));
        table.AddRow("Wait p95", S(r.Wait.P95));
        table.AddRow("Wait max", S(r.Wait.Max));
        if (r.VipWait is { } vip && r.NonVipWait is { } other)
        {
            table.AddRow("VIP wait avg / p95", $"{S(vip.Avg)} / {S(vip.P95)} ({vip.Count})");
            table.AddRow("Non-VIP wait avg / p95", $"{S(other.Avg)} / {S(other.P95)} ({other.Count})");
        }
        table.AddRow("Ride avg", S(r.Ride.Avg));
        table.AddRow("Ride max", S(r.Ride.Max));
        table.AddRow("Journey avg", S(r.Journey.Avg));
        table.AddRow("Left behind", r.LeftBehind.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Reassignments", r.Reassignments.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Floors travelled", r.FloorsTravelled.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Stops", r.Stops.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Sim time", S(r.SimTimeS));
        table.AddEmptyRow();
        table.AddRow("[grey]Wait breakdown (avg per passenger)[/]", "");
        foreach (var (bucket, seconds) in r.WaitBreakdown.Where(kv => kv.Value > 0))
            table.AddRow($"  {BenchAnalysis.BucketName(bucket)}", S(seconds));
        if (r.Ops.Any) AddOps(table, r.Ops);
        return table;
    }

    private static void AddOps(Table table, OpsReport o)
    {
        static string S(double? seconds) => seconds is { } s ? s.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "–";
        static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

        table.AddEmptyRow();
        table.AddRow("[grey]Operations[/]", "");
        table.AddRow("  Commands applied", N(o.CommandsApplied));
        table.AddRow("  Maintenance (count / time)", $"{N(o.Maintenances)} / {S(o.MaintenanceS)}");
        table.AddRow("  Emergency stops (count / time)", $"{N(o.EmergencyStops)} / {S(o.EmergencyS)}");
        table.AddRow("  Faults detected", N(o.Faults));
        table.AddRow("  Time to detect (avg / max)", $"{S(o.TimeToDetect.Avg)} / {S(o.TimeToDetect.Max)}");
        table.AddRow("  Time to recover (avg / max)", $"{S(o.TimeToRecover.Avg)} / {S(o.TimeToRecover.Max)}");
        table.AddRow("  Pickups handed back", N(o.OutOfServiceReleases));
        table.AddRow("  Passengers affected", N(o.AffectedPassengers));
        table.AddRow("  Penalty: reassignment / blocked", $"{S(o.ReassignmentWaitS)} / {S(o.BlockedWaitS)}");
        table.AddRow("  Access denials / refused", $"{N(o.AccessDenials)} / {N(o.RefusedByPolicy)}");
        foreach (var e in o.Elevators)
        {
            var parts = new List<string>();
            if (e.MaintenanceS > 0) parts.Add($"maint {S(e.MaintenanceS)} (drain {S(e.DrainS)})");
            if (e.EmergencyStops > 0) parts.Add($"emerg {N(e.EmergencyStops)}× {S(e.EmergencyS)}");
            if (e.StuckS > 0) parts.Add($"stuck {S(e.StuckS)}");
            if (e.Faults > 0) parts.Add($"faults {N(e.Faults)}");
            if (parts.Count > 0) table.AddRow($"  E{e.ElevatorId}", string.Join(", ", parts));
        }
    }
}
