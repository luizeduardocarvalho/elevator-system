using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Metrics;

namespace Elevator.Simulation.Tests;

public class CapacityScenarioTests
{
    /// <summary>
    /// Next trip (ADR-010). One elevator of capacity 2 at the lobby; three people press for floor 5 at t=0. Two board
    /// at 1 s; P3 is left behind, re-registers (same request id, same t_call) and is at once re-assigned to the full
    /// elevator for its next trip (no deferral). It boards at 27 s; its wait is attributed:
    ///   0–1   doors opening at its floor → travel_toward 1
    ///   1–14  its elevator is full (2 riders) until they get off at 5 → bypassed_full 13
    ///   14–18 that stop finishes (it started after P3's re-assignment) → serving_others 4
    ///   18–27 down to 1 and opening → travel_toward 9
    /// </summary>
    [Fact]
    public void Next_trip_left_behind_passenger_is_reassigned_at_once_and_waits_as_bypassed_full()
    {
        var result = SimEngine.Run(Three());
        Assert.Empty(result.Events.OfType<DispatchDeferred>());
        Assert.Empty(result.Events.OfType<AssignmentRejected>());

        var p3 = PassengerLifecycle.Analyze(result.Events).Single(r => r.Id == 3);
        Assert.Equal((0L, 27_000L, 1, 2, 0L), (p3.CalledAt.Ms, p3.BoardedAt!.Value.Ms, p3.LeftBehind, p3.Assignments, p3.FirstAssignedAt!.Value.Ms)); // re-assigned at 1 s: second assignment
        Assert.Equal((13.0, 4.0, 10.0), (p3.WaitBuckets[WaitBucket.BypassedFull], p3.WaitBuckets[WaitBucket.ServingOthers], p3.WaitBuckets[WaitBucket.TravelToward]));
        Assert.Equal(27.0, p3.WaitS!.Value, 6);
    }

    private static Scenario Three() => new("capacity", 1, 10, ElevatorTiming.Default, SimTime.FromSeconds(600),
        [new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5)],
        null, Elevators: 1, Capacity: 2, BypassThreshold: 1.0, Dispatch: "nearest");
}
