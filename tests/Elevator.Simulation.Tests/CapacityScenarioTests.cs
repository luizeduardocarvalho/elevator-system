using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Metrics;

namespace Elevator.Simulation.Tests;

public class CapacityScenarioTests
{
    [Fact]
    public void Defer_left_behind_passenger_keeps_id_and_t_call_and_is_served_on_the_next_trip()
    {
        var scenario = Three() with { CapacityMode = CapacityMode.Defer };

        var result = SimEngine.Run(scenario);
        var left = Assert.Single(result.Events.OfType<PassengerLeftBehind>());
        Assert.Equal((3, 1_000L), (left.PassengerId, left.At.Ms));
        Assert.DoesNotContain(result.Events.OfType<RequestSubmitted>(), r => r.At.Ms > 0);

        var p3 = PassengerLifecycle.Analyze(result.Events).Single(r => r.Id == 3);
        Assert.Equal((0L, 27_000L, 1, 2), (p3.CalledAt.Ms, p3.BoardedAt!.Value.Ms, p3.LeftBehind, p3.Assignments));
        Assert.Equal((13.0, 4.0, 10.0), (p3.WaitBuckets[WaitBucket.Blocked], p3.WaitBuckets[WaitBucket.FinishingDoorCycle], p3.WaitBuckets[WaitBucket.TravelToward]));
        Assert.Equal(27.0, p3.WaitS!.Value, 6);
    }

    [Fact]
    public void Next_trip_left_behind_passenger_is_reassigned_at_once_and_waits_as_bypassed_full()
    {
        var result = SimEngine.Run(Three());
        Assert.Empty(result.Events.OfType<DispatchDeferred>());
        Assert.Empty(result.Events.OfType<AssignmentRejected>());

        var p3 = PassengerLifecycle.Analyze(result.Events).Single(r => r.Id == 3);
        Assert.Equal((0L, 27_000L, 1, 2, 0L), (p3.CalledAt.Ms, p3.BoardedAt!.Value.Ms, p3.LeftBehind, p3.Assignments, p3.FirstAssignedAt!.Value.Ms));
        Assert.Equal((13.0, 4.0, 10.0), (p3.WaitBuckets[WaitBucket.BypassedFull], p3.WaitBuckets[WaitBucket.ServingOthers], p3.WaitBuckets[WaitBucket.TravelToward]));
        Assert.Equal(27.0, p3.WaitS!.Value, 6);
    }

    private static Scenario Three() => new("capacity", 1, 10, ElevatorTiming.Default, "look", SimTime.FromSeconds(600),
        [new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5)],
        null, Elevators: 1, Capacity: 2, BypassThreshold: 1.0, Dispatch: "nearest");
}

public class RebalanceScenarioTests
{
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
