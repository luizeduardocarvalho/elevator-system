using Elevator.Domain.Control;
using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

public class WatchdogTests
{
    private const long T = 30_000;

    private static Request R(int id, int from, int to) => Request.Create(from, to, Instant.Zero, Harness.Floors, id: id).Value!;

    private static Elevator Make() => new(0, Harness.Floors, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, 1, capacity: 10);

    [Fact]
    public void Declared_faulty_it_hands_back_its_pickups_keeps_its_riders_and_recovers_when_it_moves_again()
    {
        var e = Make();
        e.Board(R(1, 1, 6));
        e.AssignPickup(R(2, 8, 2));
        e.Tick(new Instant(3_000));
        e.Stick();
        e.Tick(new Instant(33_000));
        e.DrainEvents();

        Assert.True(e.DeclareFault(30_000));
        Assert.False(e.DeclareFault(30_000));
        var detected = e.DrainEvents();
        Assert.Equal(new FaultDetected(new Instant(33_000), 0, 2, 30_000), detected[0]);
        Assert.Equal([(2, AssignmentReason.Fault)], detected.OfType<AssignmentReleased>().Select(r => (r.RequestId, r.Reason)));
        Assert.Equal((ElevatorMode.Fault, 1), (e.Mode, e.Riders.Count));

        e.Tick(new Instant(50_000));
        e.Unstick();
        e.Tick(new Instant(51_000));
        var moved = e.DrainEvents();
        Assert.Equal(new FaultRecovered(new Instant(51_000), 0), moved[0]);
        Assert.Contains(moved, ev => ev is FloorReached { Floor: 3, At.Ms: 51_000 });
        Assert.Equal(ElevatorMode.Normal, e.Mode);
    }

    [Fact]
    public void A_stuck_elevator_with_nothing_left_to_do_recovers_as_soon_as_it_is_unstuck()
    {
        var e = Make();
        e.Stick();
        e.DeclareFault(30_000);
        e.DrainEvents();
        e.Tick(new Instant(10_000));
        e.Unstick();
        Assert.Equal(new FaultRecovered(new Instant(10_000), 0), e.DrainEvents().Single());
        Assert.Equal(ElevatorMode.Normal, e.Mode);
    }

    [Fact]
    public void A_parked_elevator_is_never_declared_faulty()
    {
        var e = Make();
        e.RequestMaintenance();
        e.DrainEvents();
        var version = e.Version;
        Assert.False(e.DeclareFault(30_000));
        Assert.Empty(e.DrainEvents());
        Assert.True(e.Version > version);
    }

    [Fact]
    public void Handler_rejects_assignments_while_faulty_and_publishes_before_releasing()
    {
        var e = Make();
        var h = new ElevatorController(e);
        var o = new Outbox();
        h.Handle(new Boarded(R(1, 1, 6)), Instant.Zero, o);
        h.Handle(new Assign(R(2, 8, 2), null, Forced: false), Instant.Zero, o);
        e.Stick();
        o.Clear();
        h.Handle(new FaultDeclared(T), new Instant(T), o);
        var toDispatcher = o.Messages.Where(m => m.To.Kind == ActorKind.Dispatcher).Select(m => m.Message).ToList();
        Assert.Equal(ElevatorMode.Fault, Assert.IsType<SnapshotPublished>(toDispatcher[0]).Snapshot.Mode);
        Assert.IsType<AssignmentReleasedMsg>(toDispatcher[1]);
        o.Clear();
        h.Handle(new Assign(R(3, 4, 9), null, Forced: true), new Instant(T + 1), o);
        Assert.Equal(AssignmentReason.Fault, o.Events.OfType<AssignmentRejected>().Single().Reason);
    }

    private sealed class LowestId : IDispatchPolicy
    {
        public string Name => "lowest";

        public DispatchChoice? Choose(DispatchContext c) =>
            c.Candidates.OrderBy(s => s.ElevatorId).Select(s => new DispatchChoice(s.ElevatorId, s.ElevatorId)).FirstOrDefault();
    }

    private static DispatcherHandler Dispatcher(int elevators = 2) =>
        new(new LowestId(), elevators, ElevatorTiming.Default, new DispatcherOptions { WatchdogTimeoutMs = T });

    private static ElevatorSnapshot Idle(int id) => new Elevator(id, Harness.Floors, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall).Snapshot();

    private static ElevatorSnapshot Moving(int id, int floor, long at, long version) =>
        Idle(id) with { CurrentFloor = floor, State = ElevatorState.MovingUp, CommittedDirection = Direction.Up, At = new Instant(at), Version = version, CarCalls = [9] };

    private static Envelope? Timer(Outbox o) => o.Messages.SingleOrDefault(m => m.Message is WatchdogTimer);

    [Fact]
    public void Elevators_without_work_are_not_watched_so_a_quiet_system_sends_nothing()
    {
        var d = Dispatcher();
        var o = new Outbox();
        d.Handle(new SnapshotPublished(Idle(0)), Instant.Zero, o);
        d.Handle(new SnapshotPublished(Idle(1)), Instant.Zero, o);
        Assert.Empty(o.Messages);
    }

    [Fact]
    public void No_progress_for_T_declares_the_elevator_faulty_once()
    {
        var d = Dispatcher();
        var o = new Outbox();
        d.Handle(new SnapshotPublished(Idle(1)), Instant.Zero, o);
        d.Handle(new SnapshotPublished(Moving(0, 2, 4_000, 5)), new Instant(4_000), o);
        Assert.Equal((Address.Dispatcher, T), (Timer(o)!.To, Timer(o)!.DelayMs));
        d.Handle(new SnapshotPublished(Moving(0, 2, 4_000, 6) with { HallCalls = [] }), new Instant(9_000), o);
        o.Clear();

        d.Handle(new WatchdogTimer(), new Instant(4_000 + T), o);
        var declared = Assert.Single(o.Messages);
        Assert.Equal((Address.Elevator(0), new FaultDeclared(T)), (declared.To, declared.Message));

        o.Clear();
        d.Handle(new SubmitRequest(R(7, 3, 8)), new Instant(4_000 + T), o);
        Assert.Equal(1, o.Events.OfType<AssignmentProposed>().Single().ElevatorId);
        d.Handle(new SnapshotPublished(Moving(0, 2, 4_000 + T, 7) with { Mode = ElevatorMode.Fault }), new Instant(4_000 + T), o);
        Assert.Null(Timer(o));
    }

    [Fact]
    public void Progress_before_the_deadline_resets_the_watch()
    {
        var d = Dispatcher(1);
        var o = new Outbox();
        d.Handle(new SnapshotPublished(Moving(0, 2, 0, 1)), Instant.Zero, o);
        d.Handle(new SnapshotPublished(Moving(0, 3, 2_000, 2)), new Instant(2_000), o);
        o.Clear();
        d.Handle(new WatchdogTimer(), new Instant(T), o);
        Assert.Equal(T + 2_000, T + Timer(o)!.DelayMs);
        Assert.DoesNotContain(o.Messages, m => m.Message is FaultDeclared);
    }
}
