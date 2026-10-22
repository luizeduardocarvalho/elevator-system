using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Metrics.Tests;

public class OpsMetricsTests
{
    private static SimTime S(double seconds) => SimTime.FromSeconds(seconds);

    private static readonly DomainEvent[] Ops =
    [
        new SimulationStarted(S(0), "ops", 1, 10, "look", 0, Elevators: 3),
        new FaultCommandApplied(S(10), 0, FaultKind.MaintenanceOn, null),
        new MaintenanceRequested(S(10), 0),
        new AssignmentReleased(S(10), 7, 0, AssignmentReason.Maintenance),
        new MaintenanceEntered(S(20), 0, 4),
        new FaultCommandApplied(S(100), 0, FaultKind.MaintenanceOff, null),
        new MaintenanceExited(S(100), 0),
        new FaultCommandApplied(S(30), 1, FaultKind.EmergencyStop, null),
        new EmergencyStopped(S(30), 1, 6),
        new AssignmentReleased(S(30), 8, 1, AssignmentReason.Emergency),
        new EmergencyCleared(S(45), 1),
        new EmergencyStopped(S(150), 1, 2),
        new FaultCommandApplied(S(50), 2, FaultKind.Stuck, 60_000),
        new FaultDetected(S(80), 2, 5, 30_000),
        new AssignmentReleased(S(80), 9, 2, AssignmentReason.Fault),
        new AssignmentReleased(S(80), 10, 2, AssignmentReason.Fault),
        new FaultRecovered(S(111), 2),
        new AccessDenied(S(5), 11, 2, "served-floors", "no"),
        new AccessDenied(S(6), 12, 0, "floor-restriction", "no"),
        new AccessDenied(S(6), 12, 1, "floor-restriction", "no"),
        new AccessDenied(S(6), 12, 2, "floor-restriction", "no"),
        new AccessDenied(S(6), 12, null, "floor-restriction", "no"),
        new SimulationEnded(S(200), 0, 0),
    ];

    [Fact]
    public void Per_elevator_out_of_service_time()
    {
        var ops = OpsMetrics.Build(Ops);
        Assert.Equal(new ElevatorOps(0, MaintenanceS: 90, DrainS: 10, EmergencyStops: 0, EmergencyS: 0, Faults: 0, StuckS: 0), ops.Elevators[0]);
        Assert.Equal(new ElevatorOps(1, 0, 0, EmergencyStops: 2, EmergencyS: 15 + 50, 0, 0), ops.Elevators[1]);
        Assert.Equal(new ElevatorOps(2, 0, 0, 0, 0, Faults: 1, StuckS: 60), ops.Elevators[2]);
    }

    [Fact]
    public void Totals_detection_recovery_releases_and_access()
    {
        var ops = OpsMetrics.Build(Ops);
        Assert.Equal((1, 90.0, 2, 65.0, 1), (ops.Maintenances, ops.MaintenanceS, ops.EmergencyStops, ops.EmergencyS, ops.Faults));
        Assert.Equal(new DurationStats(1, 30, 30, 30), ops.TimeToDetect);
        Assert.Equal(new DurationStats(1, 31, 31, 31), ops.TimeToRecover);
        Assert.Equal(4, ops.OutOfServiceReleases);
        Assert.Equal((4, 1), (ops.AccessDenials, ops.RefusedByPolicy));
        Assert.Equal(4, ops.CommandsApplied);
        Assert.True(ops.Any);
    }

    [Fact]
    public void A_detection_without_an_injected_stuck_uses_the_silence_reported()
    {
        var ops = OpsMetrics.Build([new FaultDetected(S(80), 0, 5, 42_000), new FaultRecovered(S(90), 0)]);
        Assert.Equal(42, ops.TimeToDetect.Avg);
    }

    [Fact]
    public void A_quiet_run_has_no_ops_activity() =>
        Assert.False(OpsMetrics.Build([new SimulationStarted(S(0), "q", 1, 10, "look", 0), new SimulationEnded(S(9), 0, 0)]).Any);

    [Fact]
    public void Time_assigned_to_a_stuck_elevator_is_blocked_and_out_of_service_rejections_are_blocked()
    {
        DomainEvent[] events =
        [
            new SimulationStarted(S(0), "stuck", 1, 10, "look", 2, Elevators: 2),
            new PassengerArrived(S(0), 1, 5, 9),
            new AssignmentAccepted(S(0), 1, 0),
            new StateChanged(S(0), 0, ElevatorState.Idle, ElevatorState.MovingUp, Trigger.StartMoveUp, 1),
            new FaultCommandApplied(S(2), 0, FaultKind.Stuck, 10_000),
            new PassengerArrived(S(5), 2, 3, 1),
            new AssignmentRejected(S(5), 2, 1, AssignmentReason.Emergency),
            new AssignmentAccepted(S(7), 2, 0),
            new PassengerBoarded(S(20), 1, 0, 5),
            new PassengerBoarded(S(25), 2, 0, 3),
            new PassengerAlighted(S(30), 1, 0, 9),
            new PassengerAlighted(S(35), 2, 0, 1),
            new SimulationEnded(S(35), 2, 0),
        ];
        var records = PassengerLifecycle.Analyze(events);
        Assert.Equal(10.0, records[0].WaitBuckets[WaitBucket.Blocked]);
        Assert.Equal(2 + 8.0, records[0].WaitBuckets[WaitBucket.TravelToward]);
        Assert.Equal(2 + 5.0, records[1].WaitBuckets[WaitBucket.Blocked]);
        foreach (var r in records) Assert.Equal(r.WaitS!.Value, r.WaitBuckets.Values.Sum(), 6);
    }
}
