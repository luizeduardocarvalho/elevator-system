using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Metrics;

namespace Elevator.Simulation.Tests;

public class CapacityScenarioTests
{
    /// <summary>
    /// One elevator of capacity 2 at the lobby; three people press for floor 5 at t=0. Two board at 1 s, the third
    /// is left behind, presses again (same request id, same t_call) and is picked up on the next trip.
    /// Trip: doors 0–5 s at 1, up 4 floors (5–13), stop at 5 (13–18), back down (18–26), open at 27 → P3 boards.
    /// P3's wait is 27 s from the first press:
    ///   0–1   doors opening at its floor → travel_toward 1
    ///   1–14  left behind; the full elevator rejects re-dispatch (Capacity) until riders get off at 5 → blocked 13
    ///   14–18 accepted while the stop at 5 (started at 13) finishes → finishing_door_cycle 4
    ///   18–27 travelling down to 1 and opening → travel_toward 9
    /// </summary>
    [Fact]
    public void Left_behind_passenger_keeps_id_and_t_call_and_is_served_on_the_next_trip()
    {
        var scenario = new Scenario("capacity", 1, 10, ElevatorTiming.Default, "look", SimTime.FromSeconds(600),
            [new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5), new PassengerSpec(SimTime.Zero, 1, 5)],
            null, Elevators: 1, Capacity: 2, BypassThreshold: 1.0, Dispatch: "nearest");

        var result = SimEngine.Run(scenario);
        var left = Assert.Single(result.Events.OfType<PassengerLeftBehind>());
        Assert.Equal((3, 1_000L), (left.PassengerId, left.At.Ms));
        Assert.DoesNotContain(result.Events.OfType<RequestSubmitted>(), r => r.At.Ms > 0); // re-registration is not a new request

        var p3 = PassengerLifecycle.Analyze(result.Events).Single(r => r.Id == 3);
        Assert.Equal((0L, 27_000L, 1, 2), (p3.CalledAt.Ms, p3.BoardedAt!.Value.Ms, p3.LeftBehind, p3.Assignments));
        Assert.Equal((13.0, 4.0, 10.0), (p3.WaitBuckets[WaitBucket.Blocked], p3.WaitBuckets[WaitBucket.FinishingDoorCycle], p3.WaitBuckets[WaitBucket.TravelToward]));
        Assert.Equal(27.0, p3.WaitS!.Value, 6);
    }
}
