using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Tests;

public class MaintenanceTests
{
    private static Request R(int id, int from, int to) => Request.Create(from, to, SimTime.Zero, Harness.Floors, id: id).Value!;

    private static Elevator Make(int start = 1) =>
        new(0, Harness.Floors, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, start, capacity: 10);

    [Fact]
    public void Idle_and_empty_it_parks_at_once_where_it_is()
    {
        var e = Make(start: 4);
        Assert.True(e.RequestMaintenance());
        Assert.False(e.RequestMaintenance());
        var events = e.DrainEvents();
        Assert.IsType<MaintenanceRequested>(events[0]);
        Assert.Contains(events, ev => ev is StateChanged { From: ElevatorState.Idle, To: ElevatorState.Maintenance, Trigger: Trigger.EnterMaintenance });
        Assert.Equal(new MaintenanceEntered(SimTime.Zero, 0, 4), events.OfType<MaintenanceEntered>().Single());
        Assert.Equal((ElevatorState.Maintenance, ElevatorMode.Maintenance), (e.State, e.Mode));
    }

    [Fact]
    public void With_riders_on_board_it_hands_back_pickups_delivers_its_riders_then_parks()
    {
        var e = Make();
        e.Board(R(1, 1, 6));
        e.AssignPickup(R(2, 3, 9));
        e.AssignPickup(R(3, 8, 2));
        e.Tick(new SimTime(1_000));
        e.DrainEvents();

        Assert.True(e.RequestMaintenance());
        Assert.Equal(ElevatorMode.Draining, e.Mode);
        var released = e.DrainEvents().OfType<AssignmentReleased>().ToList();
        Assert.Equal([(2, AssignmentReason.Maintenance), (3, AssignmentReason.Maintenance)], released.Select(r => (r.RequestId, r.Reason)));
        Assert.Empty(e.HallCalls);

        e.Tick(new SimTime(11_000));
        Assert.Contains(e.DrainEvents(), ev => ev is DoorsOpened { Floor: 6 });
        Assert.Equal(ElevatorMode.Draining, e.Mode);
        e.Alight(1);
        e.Tick(new SimTime(60_000));
        var parked = e.DrainEvents();
        Assert.Equal(new MaintenanceEntered(new SimTime(15_000), 0, 6), parked.OfType<MaintenanceEntered>().Single());
        Assert.Equal((ElevatorState.Maintenance, ElevatorMode.Maintenance, 6), (e.State, e.Mode, e.CurrentFloor));
    }

    [Fact]
    public void Ending_maintenance_returns_it_to_service()
    {
        var e = Make();
        e.RequestMaintenance();
        e.DrainEvents();
        Assert.True(e.EndMaintenance());
        Assert.Equal(new MaintenanceExited(SimTime.Zero, 0), e.DrainEvents().OfType<MaintenanceExited>().Single());
        Assert.Equal((ElevatorState.Idle, ElevatorMode.Normal), (e.State, e.Mode));
        Assert.False(e.EndMaintenance());
    }

    [Fact]
    public void Ending_maintenance_while_draining_cancels_the_drain()
    {
        var e = Make();
        e.Board(R(1, 1, 6));
        e.RequestMaintenance();
        Assert.True(e.EndMaintenance());
        e.Tick(new SimTime(11_000));
        e.Alight(1);
        e.Tick(new SimTime(60_000));
        Assert.DoesNotContain(e.DrainEvents(), ev => ev is MaintenanceEntered);
        Assert.Equal((ElevatorState.Idle, ElevatorMode.Normal), (e.State, e.Mode));
    }

    [Fact]
    public void Assignments_are_rejected_while_draining_and_while_parked()
    {
        var h = new ElevatorHandler(Make());
        var o = new Outbox();
        h.Handle(new Boarded(R(1, 1, 6)), SimTime.Zero, o);
        h.Handle(new ApplyFault(new FaultCommand(SimTime.Zero, 0, FaultKind.MaintenanceOn)), SimTime.Zero, o);
        o.Clear();
        h.Handle(new Assign(R(2, 3, 9), null, Forced: true), new SimTime(500), o);
        Assert.Equal(AssignmentReason.Maintenance, o.Events.OfType<AssignmentRejected>().Single().Reason);

        h.Handle(new WakeUp(), new SimTime(11_000), o);
        h.Handle(new Alighted(1), new SimTime(11_000), o);
        h.Handle(new WakeUp(), new SimTime(15_000), o);
        Assert.Contains(o.Events, ev => ev is MaintenanceEntered);
        o.Clear();
        h.Handle(new Assign(R(3, 3, 9), null, Forced: false), new SimTime(16_000), o);
        Assert.Equal(AssignmentReason.Maintenance, o.Events.OfType<AssignmentRejected>().Single().Reason);
    }

    [Fact]
    public void The_command_is_logged_first_and_the_snapshot_reaches_the_dispatcher_before_the_released_pickups()
    {
        var h = new ElevatorHandler(Make());
        var o = new Outbox();
        h.Handle(new Boarded(R(1, 1, 6)), SimTime.Zero, o);
        h.Handle(new Assign(R(2, 3, 9), null, Forced: false), SimTime.Zero, o);
        o.Clear();
        h.Handle(new ApplyFault(new FaultCommand(SimTime.Zero, 0, FaultKind.MaintenanceOn)), new SimTime(100), o);

        Assert.Equal([typeof(FaultCommandApplied), typeof(MaintenanceRequested), typeof(AssignmentReleased)], o.Events.Select(e => e.GetType()));
        var toDispatcher = o.Messages.Where(m => m.To.Kind == ActorKind.Dispatcher).Select(m => m.Message).ToList();
        Assert.Equal(ElevatorMode.Draining, Assert.IsType<SnapshotPublished>(toDispatcher[0]).Snapshot.Mode);
        Assert.IsType<AssignmentReleasedMsg>(toDispatcher[1]);
    }
}
