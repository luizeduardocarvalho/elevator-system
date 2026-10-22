using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Metrics;

namespace Elevator.Simulation.Tests;

public class CapacityScenarioTests
{
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

    private static Scenario Three() => new("capacity", 1, 10, ElevatorTiming.Default, SimTime.FromSeconds(600),
        [new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5)],
        null, Elevators: 1, Capacity: 2, BypassThreshold: 1.0, Dispatch: "nearest");
}
