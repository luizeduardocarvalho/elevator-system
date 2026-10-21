using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Tests;

/// <summary>A stuck drive (ADR-013): its timed phases freeze, and resume with the time they had left.</summary>
public class StuckMechanicsTests
{
    private static Request R(int id, int from, int to) => Request.Create(from, to, SimTime.Zero, Harness.Floors, id: id).Value!;

    private static Elevator Make() => new(0, Harness.Floors, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, 1, capacity: 10);

    [Fact]
    public void Phases_freeze_while_stuck_and_resume_with_the_time_left()
    {
        var e = Make();
        e.Board(R(1, 1, 3));            // 1 -> 2 at 2 s, 2 -> 3 at 4 s
        e.Tick(new SimTime(2_500));     // passed 2, half a second into the next segment
        Assert.True(e.Stick());
        Assert.False(e.Stick());        // already stuck
        Assert.Null(e.NextDeadline);
        Assert.Null(e.Snapshot().PhaseEndsAt);

        e.Tick(new SimTime(60_000));    // a minute later: still between 2 and 3
        Assert.DoesNotContain(e.DrainEvents(), ev => ev is FloorReached { Floor: 3 });
        Assert.Equal((2, ElevatorState.MovingUp), (e.CurrentFloor, e.State));

        Assert.True(e.Unstick());
        Assert.Equal(new SimTime(61_500), e.NextDeadline); // the 1.5 s it had left
        e.Tick(new SimTime(61_500));
        Assert.Contains(e.DrainEvents(), ev => ev is FloorReached { Floor: 3, At.Ms: 61_500 });
    }

    [Fact]
    public void Stuck_while_idle_it_may_start_a_phase_that_does_not_progress()
    {
        var e = Make();
        Assert.True(e.Stick());
        e.AssignPickup(R(1, 4, 9));
        Assert.Equal(ElevatorState.MovingUp, e.State); // the controller commands it, the drive does not move
        e.Tick(new SimTime(30_000));
        Assert.Equal(1, e.CurrentFloor);
        e.Unstick();
        e.Tick(new SimTime(30_000 + 2_000));
        Assert.Equal(2, e.CurrentFloor); // a full floor of travel after the unstick
    }

    [Fact]
    public void Doors_reopening_after_a_stuck_closing_take_as_long_as_they_had_closed()
    {
        var e = Make();
        e.AssignPickup(R(1, 1, 5));     // opens at once at 1 (open 1 s, dwell 3 s, closing from 4 s)
        e.Tick(new SimTime(4_400));     // closing for 0.4 s
        e.Stick();
        e.Tick(new SimTime(20_000));
        e.Unstick();
        e.Tick(new SimTime(20_200));    // closing for 0.6 s in total
        e.AssignPickup(R(2, 1, 6));     // same floor, same direction: reopen
        Assert.Equal(ElevatorState.DoorOpening, e.State);
        Assert.Equal(new SimTime(20_800), e.NextDeadline); // 0.6 s to open again (opening and closing both 1 s)
    }

    [Fact]
    public void Handler_applies_a_timed_stuck_and_unsticks_it_on_time()
    {
        var e = Make();
        var h = new ElevatorHandler(e);
        var o = new Outbox();
        h.Handle(new Boarded(R(1, 1, 3)), SimTime.Zero, o);
        o.Clear();
        h.Handle(new ApplyFault(new FaultCommand(new SimTime(1_000), 0, FaultKind.Stuck, 10_000)), new SimTime(1_000), o);
        Assert.Equal(new FaultCommandApplied(new SimTime(1_000), 0, FaultKind.Stuck, 10_000), o.Events.OfType<FaultCommandApplied>().Single());
        Assert.Equal(new SimTime(11_000), h.NextWakeUp);

        o.Clear();
        h.Handle(new WakeUp(), new SimTime(11_000), o); // unstuck: 1 s of travel left
        Assert.Equal(new SimTime(12_000), h.NextWakeUp);
        h.Handle(new WakeUp(), new SimTime(12_000), o);
        Assert.Contains(o.Events, ev => ev is FloorReached { Floor: 2, At.Ms: 12_000 });
    }

    [Fact]
    public void Commands_that_change_nothing_are_not_logged()
    {
        var h = new ElevatorHandler(Make());
        var o = new Outbox();
        h.Handle(new ApplyFault(new FaultCommand(SimTime.Zero, 0, FaultKind.Unstick)), SimTime.Zero, o);
        Assert.Empty(o.Events.OfType<FaultCommandApplied>());
    }
}
