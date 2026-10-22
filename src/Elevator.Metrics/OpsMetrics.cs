using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Metrics;

/// <summary>One elevator's out-of-service time in a run (sim seconds, ADR-013).</summary>
/// <param name="MaintenanceS">From MaintenanceRequested to MaintenanceExited (draining included), or to the end of the run.</param>
/// <param name="DrainS">The draining part: from MaintenanceRequested to MaintenanceEntered (or to the drain being cancelled).</param>
/// <param name="EmergencyS">From EmergencyStopped to EmergencyCleared, or to the end of the run.</param>
/// <param name="StuckS">Time its drive was stuck (injected Stuck commands: their duration, or until Unstick).</param>
public sealed record ElevatorOps(int ElevatorId, double MaintenanceS, double DrainS, int EmergencyStops, double EmergencyS, int Faults, double StuckS);

/// <summary>
/// Operations metrics of a run (ADR-013), all from the event log: maintenance, emergency stops, faults with the time to
/// detect them (stuck → FaultDetected; without an injected stuck, the silence the watchdog reported) and to recover
/// (FaultDetected → FaultRecovered), pickups handed back because an elevator went out of service, and access refusals.
/// The wait penalty of reassignments is the <c>reassignment</c> + <c>blocked</c> bucket time (ADR-008), filled in by
/// <see cref="ReportBuilder"/> from the passengers' attribution.
/// </summary>
public sealed record OpsReport(
    IReadOnlyList<ElevatorOps> Elevators,
    int Maintenances,
    double MaintenanceS,
    int EmergencyStops,
    double EmergencyS,
    int Faults,
    DurationStats TimeToDetect,
    DurationStats TimeToRecover,
    int OutOfServiceReleases,
    int AccessDenials,
    int RefusedByPolicy,
    int CommandsApplied)
{
    public static OpsReport Empty { get; } = new([], 0, 0, 0, 0, 0, DurationStats.From([]), DurationStats.From([]), 0, 0, 0, 0);

    /// <summary>Passengers with a pickup handed back or refused because an elevator was out of service.</summary>
    public int AffectedPassengers { get; init; }

    /// <summary>Total <c>reassignment</c> bucket seconds over delivered passengers.</summary>
    public double ReassignmentWaitS { get; init; }

    /// <summary>Total <c>blocked</c> bucket seconds over delivered passengers.</summary>
    public double BlockedWaitS { get; init; }

    /// <summary>Anything operational happened: commands, faults, out-of-service time or access refusals.</summary>
    public bool Any => CommandsApplied > 0 || Maintenances > 0 || EmergencyStops > 0 || Faults > 0 || AccessDenials > 0 || RefusedByPolicy > 0;
}

public static class OpsMetrics
{
    private static bool OutOfService(AssignmentReason r) => r is AssignmentReason.Maintenance or AssignmentReason.Emergency or AssignmentReason.Fault;

    private sealed class Tracker(int id)
    {
        public readonly int Id = id;
        public long MaintenanceMs, DrainMs, EmergencyMs, StuckMs;
        public int EmergencyStops, Faults;
        public long? MaintenanceFrom, DrainFrom, EmergencyFrom, DetectedAt;
        public long? StuckFrom;
        public long StuckUntil = long.MaxValue;
        public long? LastStuckStart;

        public void EndStuck(long at)
        {
            if (StuckFrom is not { } from) return;
            StuckMs += Math.Max(0, Math.Min(at, StuckUntil) - from);
            StuckFrom = null;
        }

        public ElevatorOps ToOps() =>
            new(Id, MaintenanceMs / 1000.0, DrainMs / 1000.0, EmergencyStops, EmergencyMs / 1000.0, Faults, StuckMs / 1000.0);
    }

    public static OpsReport Build(IEnumerable<DomainEvent> events)
    {
        var trackers = new SortedDictionary<int, Tracker>();
        Tracker E(int id) => trackers.TryGetValue(id, out var t) ? t : trackers[id] = new Tracker(id);
        var detect = new List<double>();
        var recover = new List<double>();
        int maintenances = 0, releases = 0, denials = 0, refused = 0, commands = 0;
        long end = 0;

        foreach (var e in events)
        {
            end = Math.Max(end, e.At.Ms);
            var at = e.At.Ms;
            switch (e)
            {
                case RunStarted started:
                    for (var i = 0; i < started.Elevators; i++) E(i);
                    break;
                case FaultCommandApplied { Kind: FaultKind.Stuck } stuck:
                    var st = E(stuck.ElevatorId);
                    st.EndStuck(at);
                    st.StuckFrom = at;
                    st.LastStuckStart = at;
                    st.StuckUntil = stuck.DurationMs is { } d ? at + d : long.MaxValue;
                    commands++;
                    break;
                case FaultCommandApplied { Kind: FaultKind.Unstick } unstick:
                    E(unstick.ElevatorId).EndStuck(at);
                    commands++;
                    break;
                case FaultCommandApplied:
                    commands++;
                    break;
                case MaintenanceRequested m:
                    maintenances++;
                    E(m.ElevatorId).MaintenanceFrom = at;
                    E(m.ElevatorId).DrainFrom = at;
                    break;
                case MaintenanceEntered m when E(m.ElevatorId).DrainFrom is { } from:
                    E(m.ElevatorId).DrainMs += at - from;
                    E(m.ElevatorId).DrainFrom = null;
                    break;
                case MaintenanceExited m:
                    var mt = E(m.ElevatorId);
                    if (mt.MaintenanceFrom is { } since) mt.MaintenanceMs += at - since;
                    if (mt.DrainFrom is { } drainSince) mt.DrainMs += at - drainSince;
                    mt.MaintenanceFrom = mt.DrainFrom = null;
                    break;
                case EmergencyStopped s:
                    E(s.ElevatorId).EmergencyStops++;
                    E(s.ElevatorId).EmergencyFrom = at;
                    break;
                case EmergencyCleared c when E(c.ElevatorId).EmergencyFrom is { } from:
                    E(c.ElevatorId).EmergencyMs += at - from;
                    E(c.ElevatorId).EmergencyFrom = null;
                    break;
                case FaultDetected f:
                    var ft = E(f.ElevatorId);
                    ft.Faults++;
                    ft.DetectedAt = at;
                    detect.Add((ft.LastStuckStart is { } stuckAt && stuckAt <= at ? at - stuckAt : f.SilentForMs) / 1000.0);
                    break;
                case FaultRecovered r when E(r.ElevatorId).DetectedAt is { } detectedAt:
                    recover.Add((at - detectedAt) / 1000.0);
                    E(r.ElevatorId).DetectedAt = null;
                    break;
                case AssignmentReleased { Reason: var reason } when OutOfService(reason):
                    releases++;
                    break;
                case AccessDenied { ElevatorId: null }:
                    refused++;
                    break;
                case AccessDenied:
                    denials++;
                    break;
            }
        }

        foreach (var t in trackers.Values)
        {
            if (t.MaintenanceFrom is { } m) t.MaintenanceMs += end - m;
            if (t.DrainFrom is { } d) t.DrainMs += end - d;
            if (t.EmergencyFrom is { } em) t.EmergencyMs += end - em;
            t.EndStuck(end);
        }

        var elevators = trackers.Values.Select(t => t.ToOps()).ToList();
        return new OpsReport(elevators, maintenances, elevators.Sum(e => e.MaintenanceS), elevators.Sum(e => e.EmergencyStops),
            elevators.Sum(e => e.EmergencyS), elevators.Sum(e => e.Faults), DurationStats.From(detect), DurationStats.From(recover),
            releases, denials, refused, commands);
    }

    /// <summary>Passengers who had a pickup handed back, or an assignment refused, because an elevator was out of service.</summary>
    public static int AffectedPassengers(IEnumerable<DomainEvent> events) =>
        events.Select(e => e switch
            {
                AssignmentReleased r when OutOfService(r.Reason) => (int?)r.RequestId,
                AssignmentRejected r when OutOfService(r.Reason) => r.RequestId,
                _ => null,
            })
            .OfType<int>().Distinct().Count();
}
