using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Tests;

public class EmergencyTests
{
    private static Request R(int id, int from, int to) => Request.Create(from, to, Instant.Zero, Harness.Floors, id: id).Value!;

    private static Elevator Make(int start = 1) =>
        new(0, Harness.Floors, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, start, capacity: 10);

    [Fact]
    public void Moving_it_halts_at_the_next_floor_with_the_doors_closed_and_stays()
    {
        var e = Make();
        e.Board(R(1, 1, 8));
        e.AssignPickup(R(2, 6, 9));
        e.Tick(new Instant(3_000));
        e.DrainEvents();

        Assert.True(e.EmergencyStop());
        Assert.False(e.EmergencyStop());
        Assert.Equal(ElevatorMode.Emergency, e.Mode);
        var stop = e.DrainEvents();
        Assert.Equal(new EmergencyStopped(new Instant(3_000), 0, 2), stop[0]);
        Assert.Equal((2, AssignmentReason.Emergency), stop.OfType<AssignmentReleased>().Select(r => (r.RequestId, r.Reason)).Single());

        e.Tick(new Instant(120_000));
        var after = e.DrainEvents();
        Assert.Equal(new StateChanged(new Instant(4_000), 0, ElevatorState.MovingUp, ElevatorState.Idle, Trigger.EmergencyHalt, 3), after.OfType<StateChanged>().Single());
        Assert.DoesNotContain(after, ev => ev is DoorsOpened);
        Assert.Equal((3, ElevatorState.Idle, 1), (e.CurrentFloor, e.State, e.Riders.Count));
        Assert.Null(e.NextDeadline);
    }

    [Fact]
    public void Cleared_it_resumes_and_delivers_its_riders()
    {
        var e = Make();
        e.Board(R(1, 1, 8));
        e.Tick(new Instant(3_000));
        e.EmergencyStop();
        e.Tick(new Instant(60_000));
        e.DrainEvents();

        Assert.True(e.ClearEmergency());
        Assert.False(e.ClearEmergency());
        Assert.Equal(new EmergencyCleared(new Instant(60_000), 0), e.DrainEvents().OfType<EmergencyCleared>().Single());
        e.Tick(new Instant(120_000));
        Assert.Contains(e.DrainEvents(), ev => ev is DoorsOpened { Floor: 8, At.Ms: 60_000 + 5 * 2_000 + 1_000 });
        Assert.Equal(ElevatorMode.Normal, e.Mode);
    }

    [Fact]
    public void Doors_open_close_at_once_and_stay_closed()
    {
        var e = Make();
        e.AssignPickup(R(1, 1, 5));
        e.Tick(new Instant(2_000));
        e.DrainEvents();
        e.EmergencyStop();
        Assert.Contains(e.DrainEvents(), ev => ev is StateChanged { From: ElevatorState.DoorOpen, To: ElevatorState.DoorClosing, Trigger: Trigger.EmergencyClose });
        e.Tick(new Instant(60_000));
        Assert.Equal([ElevatorState.Idle], e.DrainEvents().OfType<StateChanged>().Select(s => s.To));
    }

    [Fact]
    public void Opening_doors_reverse_and_the_riders_for_that_floor_get_off_when_cleared()
    {
        var e = Make();
        e.Board(R(1, 1, 3));
        e.Tick(new Instant(4_400));
        e.DrainEvents();
        e.EmergencyStop();
        Assert.Equal(new Instant(4_800), e.NextDeadline);
        e.Tick(new Instant(30_000));
        Assert.DoesNotContain(e.DrainEvents(), ev => ev is DoorsOpened);

        e.ClearEmergency();
        e.Tick(new Instant(31_000));
        Assert.Contains(e.DrainEvents(), ev => ev is DoorsOpened { Floor: 3, At.Ms: 31_000 });
    }

    /// <summary>
    /// Cleared while the reversed doors are still closing: the stop was never served (its car call was consumed when the
    /// doors started opening), so when they finish closing the doors must open again here. Before the fix the elevator
    /// went idle with the rider still on board, for good (found by HardStageProperties; see StrandedRiderTests).
    /// </summary>
    [Fact]
    public void Opening_doors_reversed_and_cleared_before_they_close_reopen_for_the_riders_of_that_floor()
    {
        var e = Make();
        e.Board(R(1, 1, 3));
        e.Tick(new Instant(4_400));
        e.EmergencyStop();
        e.Tick(new Instant(4_600));
        e.ClearEmergency();
        e.DrainEvents();

        e.Tick(new Instant(30_000));
        var after = e.DrainEvents();
        Assert.Contains(after, ev => ev is StateChanged { From: ElevatorState.DoorClosing, To: ElevatorState.Idle, At.Ms: 4_800 });
        Assert.Contains(after, ev => ev is DoorsOpened { Floor: 3, At.Ms: 5_800 });
    }

    [Fact]
    public void Halted_at_a_riders_floor_it_opens_there_first_when_cleared()
    {
        var e = Make();
        e.Board(R(1, 1, 3));
        e.Board(R(2, 1, 9));
        e.Tick(new Instant(3_000));
        e.EmergencyStop();
        e.Tick(new Instant(10_000));
        e.DrainEvents();
        e.ClearEmergency();
        e.Tick(new Instant(11_000));
        Assert.Contains(e.DrainEvents(), ev => ev is DoorsOpened { Floor: 3 });
    }

    [Fact]
    public void Ignored_while_parked_for_maintenance()
    {
        var e = Make();
        e.RequestMaintenance();
        Assert.False(e.EmergencyStop());
        Assert.Equal(ElevatorMode.Maintenance, e.Mode);
    }

    [Fact]
    public void While_draining_the_emergency_wins_and_draining_resumes_when_cleared()
    {
        var e = Make();
        e.Board(R(1, 1, 4));
        e.RequestMaintenance();
        e.EmergencyStop();
        Assert.Equal(ElevatorMode.Emergency, e.Mode);
        e.Tick(new Instant(30_000));
        e.ClearEmergency();
        Assert.Equal(ElevatorMode.Draining, e.Mode);
        e.Tick(new Instant(60_000));
        e.Alight(1);
        e.Tick(new Instant(90_000));
        Assert.Equal(ElevatorMode.Maintenance, e.Mode);
    }

    [Fact]
    public void Handler_rejects_assignments_with_reason_emergency_and_logs_commands()
    {
        var h = new ElevatorController(Make());
        var o = new Outbox();
        h.Handle(new ApplyFault(new FaultCommand(Instant.Zero, 0, FaultKind.EmergencyStop)), Instant.Zero, o);
        Assert.Equal([typeof(FaultCommandApplied), typeof(EmergencyStopped)], o.Events.Select(e => e.GetType()));
        o.Clear();
        h.Handle(new Assign(R(1, 3, 9), null, Forced: true), new Instant(10), o);
        Assert.Equal(AssignmentReason.Emergency, o.Events.OfType<AssignmentRejected>().Single().Reason);
        o.Clear();
        h.Handle(new ApplyFault(new FaultCommand(Instant.Zero, 0, FaultKind.EmergencyClear)), new Instant(20), o);
        Assert.Equal([typeof(FaultCommandApplied), typeof(EmergencyCleared)], o.Events.Select(e => e.GetType()));
    }
}
