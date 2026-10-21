using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Metrics;

namespace Elevator.Simulation.Tests;

public class CapacityScenarioTests
{
    /// <summary>
    /// Defer mode (before ADR-010). One elevator of capacity 2 at the lobby; three people press for floor 5 at t=0.
    /// Two board at 1 s, the third is left behind, presses again (same request id, same t_call) and is picked up on
    /// the next trip.
    /// Trip: doors 0–5 s at 1, up 4 floors (5–13), stop at 5 (13–18), back down (18–26), open at 27 → P3 boards.
    /// P3's wait is 27 s from the first press:
    ///   0–1   doors opening at its floor → travel_toward 1
    ///   1–14  left behind; the full elevator rejects re-dispatch (Capacity) until riders get off at 5 → blocked 13
    ///   14–18 accepted while the stop at 5 (started at 13) finishes → finishing_door_cycle 4
    ///   18–27 travelling down to 1 and opening → travel_toward 9
    /// </summary>
    [Fact]
    public void Defer_left_behind_passenger_keeps_id_and_t_call_and_is_served_on_the_next_trip()
    {
        var scenario = Three() with { CapacityMode = CapacityMode.Defer };

        var result = SimEngine.Run(scenario);
        var left = Assert.Single(result.Events.OfType<PassengerLeftBehind>());
        Assert.Equal((3, 1_000L), (left.PassengerId, left.At.Ms));
        Assert.DoesNotContain(result.Events.OfType<RequestSubmitted>(), r => r.At.Ms > 0); // re-registration is not a new request

        var p3 = PassengerLifecycle.Analyze(result.Events).Single(r => r.Id == 3);
        Assert.Equal((0L, 27_000L, 1, 2), (p3.CalledAt.Ms, p3.BoardedAt!.Value.Ms, p3.LeftBehind, p3.Assignments));
        Assert.Equal((13.0, 4.0, 10.0), (p3.WaitBuckets[WaitBucket.Blocked], p3.WaitBuckets[WaitBucket.FinishingDoorCycle], p3.WaitBuckets[WaitBucket.TravelToward]));
        Assert.Equal(27.0, p3.WaitS!.Value, 6);
    }

    /// <summary>
    /// The same in NextTrip mode (ADR-010): P3, left behind at 1 s, re-registers and is at once re-assigned to the full
    /// elevator for its next trip (no deferral). Same boarding time; the wait is attributed differently:
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

    private static Scenario Three() => new("capacity", 1, 10, ElevatorTiming.Default, "look", SimTime.FromSeconds(600),
        [new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5)],
        null, Elevators: 1, Capacity: 2, BypassThreshold: 1.0, Dispatch: "nearest");
}

public class RebalanceScenarioTests
{
    /// <summary>
    /// Lunch traffic (seed 3) with LOOK + Nearest + rebalancing, where Nearest leaves room for improvement so moves
    /// actually happen. Every move must appear as Released(Rebalance) followed by a new acceptance of the same request,
    /// no request moves more than twice, and everyone is still served exactly once.
    /// </summary>
    [Fact]
    public void Rebalanced_requests_are_released_then_reassigned_and_served_once()
    {
        var scenario = Repo.Load("lunch.yaml") with { Seed = 3 };
        var result = SimEngine.Run(scenario, new Strategy("look", "nearest", Rebalance: true));

        var moves = result.Events.OfType<AssignmentReleased>().Where(r => r.Reason == AssignmentReason.Rebalance).ToList();
        Assert.NotEmpty(moves);
        foreach (var move in moves)
        {
            var after = result.Events.SkipWhile(e => e != move).Skip(1);
            Assert.Contains(after, e => e is AssignmentAccepted a && a.RequestId == move.RequestId);
        }
        Assert.Equal(0, result.Unserved);
        Assert.All(PassengerLifecycle.Analyze(result.Events), p => Assert.True(p.Reassignments <= 2));
    }
}
