using Elevator.Domain.Control;
using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

/// <summary>
/// Loads above one unit (freight, ADR-012). Found by the Hard stage's invariant properties: without these rules a load
/// that cannot fit made the elevator reopen for it forever. With one-unit loads none of them changes anything.
/// </summary>
public class HeavyLoadTests
{
    private static Request R(int id, int from, int to, int load = 1) => Request.Create(from, to, SimTime.Zero, Harness.Floors, id: id, load: load).Value!;

    private static Elevator Make(int capacity) =>
        new(0, Harness.Floors, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, 1, capacity, bypassThreshold: 1.0);

    [Fact]
    public void A_pickup_whose_load_does_not_fit_is_bypassed_until_riders_get_off()
    {
        var e = Make(capacity: 6);
        e.Board(R(1, 1, 5, load: 4));       // 2 units of room left
        e.AssignPickup(R(2, 1, 8, load: 3)); // right here, but 3 does not fit
        Assert.Equal(ElevatorState.MovingUp, e.State); // goes to drop off first instead of opening for it
        e.Tick(new SimTime(9_000));          // at 5, doors open
        e.Alight(1);
        e.Tick(new SimTime(60_000));
        Assert.Contains(e.DrainEvents(), ev => ev is HallCallAnswered { Floor: 1 }); // came back for it
    }

    [Fact]
    public void A_new_pickup_at_the_open_door_that_does_not_fit_is_not_answered_at_once()
    {
        var e = Make(capacity: 6);
        e.Board(R(1, 1, 5, load: 4));
        e.Tick(new SimTime(9_000));          // doors open at 5 (rider still inside)
        e.DrainEvents();
        e.AssignPickup(R(2, 5, 9, load: 3));
        Assert.DoesNotContain(e.DrainEvents(), ev => ev is HallCallAnswered);
    }

    [Fact]
    public void Passengers_board_in_strict_arrival_order_nobody_overtakes_a_load_that_does_not_fit()
    {
        var p = new PassengersHandler(5);
        var o = new Outbox();
        foreach (var r in new[] { R(1, 3, 9, load: 4), R(2, 3, 9, load: 3), R(3, 3, 9, load: 1) })
        {
            p.Handle(new PassengerArrives(r), SimTime.Zero, o);
            p.Handle(new AssignmentAcceptedMsg(r.Id, 0), SimTime.Zero, o);
        }
        o.Clear();
        p.Handle(new ElevatorNotice(new HallCallAnswered(SimTime.Zero, 0, 3, Direction.Up)), SimTime.Zero, o);
        Assert.Equal([1], o.Events.OfType<PassengerBoarded>().Select(b => b.PassengerId));
        Assert.Equal([2, 3], o.Events.OfType<PassengerLeftBehind>().Select(b => b.PassengerId)); // 3 would fit, but 2 is ahead
    }
}
