using Elevator.Domain.Events;

namespace Elevator.Metrics;

/// <summary>Builds a <see cref="RunReport"/> from the event log alone. It never touches the elevators.</summary>
public static class ReportBuilder
{
    public static RunReport Build(IEnumerable<DomainEvent> events)
    {
        var list = events as IReadOnlyList<DomainEvent> ?? events.ToList();
        var started = list.OfType<SimulationStarted>().FirstOrDefault();
        var ended = list.OfType<SimulationEnded>().LastOrDefault();
        var records = PassengerLifecycle.Analyze(list);
        var delivered = records.Where(r => r.Delivered && r.BoardedAt is not null).ToList();
        var passengers = started?.Passengers ?? records.Count;
        var refused = list.Count(e => e is AccessDenied { ElevatorId: null });
        var vips = list.OfType<PassengerArrived>().Where(a => a.IsVip).Select(a => a.PassengerId).ToHashSet();

        var breakdown = Enum.GetValues<WaitBucket>().ToDictionary(
            b => b,
            b => delivered.Count == 0 ? 0 : delivered.Average(r => r.WaitBuckets[b]));

        return new RunReport(
            started?.Scenario ?? "?",
            started?.Scheduling ?? "?",
            started?.Seed ?? 0,
            passengers,
            delivered.Count,
            passengers - delivered.Count - refused,
            list.Count(e => e is RequestRejected),
            DurationStats.From(delivered.Select(r => r.WaitS!.Value).ToList()),
            DurationStats.From(delivered.Select(r => r.RideS!.Value).ToList()),
            list.Count(e => e is FloorReached),
            list.Count(e => e is DoorsOpened),
            (ended?.At.Ms ?? 0) / 1000.0)
        {
            Dispatch = started?.Dispatch ?? "none",
            Elevators = started?.Elevators ?? 1,
            Journey = DurationStats.From(delivered.Select(r => r.JourneyS!.Value).ToList()),
            LeftBehind = records.Sum(r => r.LeftBehind),
            Reassignments = records.Sum(r => r.Reassignments),
            Rejections = records.Sum(r => r.Rejections),
            WaitBreakdown = breakdown,
            RefusedByPolicy = refused,
            VipWait = vips.Count == 0 ? null : DurationStats.From(delivered.Where(r => vips.Contains(r.Id)).Select(r => r.WaitS!.Value).ToList()),
            NonVipWait = vips.Count == 0 ? null : DurationStats.From(delivered.Where(r => !vips.Contains(r.Id)).Select(r => r.WaitS!.Value).ToList()),
            Ops = OpsMetrics.Build(list) with
            {
                AffectedPassengers = OpsMetrics.AffectedPassengers(list),
                ReassignmentWaitS = delivered.Sum(r => r.WaitBuckets[WaitBucket.Reassignment]),
                BlockedWaitS = delivered.Sum(r => r.WaitBuckets[WaitBucket.Blocked]),
            },
        };
    }
}
