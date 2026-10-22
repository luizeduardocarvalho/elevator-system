using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Tests;

public class StuckMechanicsTests
{
    private static Request R(int id, int from, int to) => Request.Create(from, to, Instant.Zero, Harness.Floors, id: id).Value!;

    private static Elevator Make() => new(0, Harness.Floors, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, 1, capacity: 10);

    [Fact]
    public void Phases_freeze_while_stuck_and_resume_with_the_time_left()
    {
        var e = Make();
        e.Board(R(1, 1, 3));
        e.Tick(new Instant(2_500));
        Assert.True(e.Stick());
        Assert.False(e.Stick());
        Assert.Null(e.NextDeadline);
        Assert.Null(e.Snapshot().PhaseEndsAt);

        e.Tick(new Instant(60_000));
        Assert.DoesNotContain(e.DrainEvents(), ev => ev is FloorReached { Floor: 3 });
        Assert.Equal((2, ElevatorState.MovingUp), (e.CurrentFloor, e.State));

        Assert.True(e.Unstick());
        Assert.Equal(new Instant(61_500), e.NextDeadline);
        e.Tick(new Instant(61_500));
        Assert.Contains(e.DrainEvents(), ev => ev is FloorReached { Floor: 3, At.Ms: 61_500 });
    }

    [Fact]
    public void Stuck_while_idle_it_may_start_a_phase_that_does_not_progress()
    {
        var e = Make();
        Assert.True(e.Stick());
        e.AssignPickup(R(1, 4, 9));
        Assert.Equal(ElevatorState.MovingUp, e.State);
        e.Tick(new Instant(30_000));
        Assert.Equal(1, e.CurrentFloor);
        e.Unstick();
        e.Tick(new Instant(30_000 + 2_000));
        Assert.Equal(2, e.CurrentFloor);
    }

    [Fact]
    public void Doors_reopening_after_a_stuck_closing_take_as_long_as_they_had_closed()
    {
        var e = Make();
        e.AssignPickup(R(1, 1, 5));
        e.Tick(new Instant(4_400));
        e.Stick();
        e.Tick(new Instant(20_000));
        e.Unstick();
        e.Tick(new Instant(20_200));
        e.AssignPickup(R(2, 1, 6));
        Assert.Equal(ElevatorState.DoorOpening, e.State);
        Assert.Equal(new Instant(20_800), e.NextDeadline);
    }

    [Fact]
    public void Handler_applies_a_timed_stuck_and_unsticks_it_on_time()
    {
        var e = Make();
        var h = new ElevatorHandler(e);
        var o = new Outbox();
        h.Handle(new Boarded(R(1, 1, 3)), Instant.Zero, o);
        o.Clear();
        h.Handle(new ApplyFault(new FaultCommand(new Instant(1_000), 0, FaultKind.Stuck, 10_000)), new Instant(1_000), o);
        Assert.Equal(new FaultCommandApplied(new Instant(1_000), 0, FaultKind.Stuck, 10_000), o.Events.OfType<FaultCommandApplied>().Single());
        Assert.Equal(new Instant(11_000), h.NextWakeUp);

        o.Clear();
        h.Handle(new WakeUp(), new Instant(11_000), o);
        Assert.Equal(new Instant(12_000), h.NextWakeUp);
        h.Handle(new WakeUp(), new Instant(12_000), o);
        Assert.Contains(o.Events, ev => ev is FloorReached { Floor: 2, At.Ms: 12_000 });
    }

    [Fact]
    public void Commands_that_change_nothing_are_not_logged()
    {
        var h = new ElevatorHandler(Make());
        var o = new Outbox();
        h.Handle(new ApplyFault(new FaultCommand(Instant.Zero, 0, FaultKind.Unstick)), Instant.Zero, o);
        Assert.Empty(o.Events.OfType<FaultCommandApplied>());
    }
}
