using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Tests;

/// <summary>
/// Oldest-first re-dispatch (ADR-006, ADR-015): a request handed back keeps its place by call time, not by where it
/// sat in the elevator's stop list, and requests that were already waiting are served before it.
/// </summary>
public class RedispatchOrderTests
{
    private static Request R(int id, int from, long calledAtMs) => Request.Create(from, from + 2, new SimTime(calledAtMs), 12, id: id).Value!;

    [Fact]
    public void A_hand_back_batch_is_emitted_oldest_call_first_whatever_the_stop_order()
    {
        var h = new ElevatorHandler(new Elevator(0, 12, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall));
        var o = new Outbox();
        h.Handle(new Start(), SimTime.Zero, o);
        // Stops registered in the order C, A, B; calls made in the order A, B, C.
        h.Handle(new Assign(R(3, 9, calledAtMs: 30_000), null, Forced: false), new SimTime(40_000), o);
        h.Handle(new Assign(R(1, 5, calledAtMs: 10_000), null, Forced: false), new SimTime(40_000), o);
        h.Handle(new Assign(R(2, 7, calledAtMs: 20_000), null, Forced: false), new SimTime(40_000), o);
        o.Clear();

        h.Handle(new ApplyFault(new FaultCommand(new SimTime(40_000), 0, FaultKind.MaintenanceOn, null)), new SimTime(40_000), o);

        Assert.Equal([1, 2, 3], o.Events.OfType<AssignmentReleased>().Select(e => e.RequestId));
        Assert.Equal([1, 2, 3], o.Messages.Where(m => m.To.Kind == ActorKind.Dispatcher).Select(m => m.Message)
            .OfType<AssignmentReleasedMsg>().Select(m => m.RequestId));
    }

    /// <summary>Picks the lowest-id candidate.</summary>
    private sealed class First : IDispatchPolicy
    {
        public string Name => "first";

        public DispatchChoice? Choose(DispatchContext c) =>
            c.Candidates.OrderBy(s => s.ElevatorId).Select(s => new DispatchChoice(s.ElevatorId, 0, c.Now)).FirstOrDefault();
    }

    [Fact]
    public void A_request_already_waiting_is_served_before_one_handed_back()
    {
        var snapshot = new Elevator(0, 12, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall).Snapshot();
        var d = new DispatcherHandler(new First(), 1, ElevatorTiming.Default);
        var o = new Outbox();
        d.Handle(new SnapshotPublished(snapshot), SimTime.Zero, o);

        // The handed-back request (called at 20 s) is assigned to E0.
        d.Handle(new SubmitRequest(R(2, 7, calledAtMs: 20_000)), new SimTime(20_000), o);
        d.Handle(new AssignmentAcceptedMsg(2, 0, 1), new SimTime(20_000), o);
        // An older request (called at 10 s, delayed in arriving) is rejected for capacity: it waits, E0 excluded.
        d.Handle(new SubmitRequest(R(1, 5, calledAtMs: 10_000)), new SimTime(21_000), o);
        d.Handle(new AssignmentRejectedMsg(1, 0, AssignmentReason.Capacity), new SimTime(21_000), o);
        o.Clear();

        // E0 hands request 2 back: its snapshot arrives first (ADR-015 fix 1), then the release.
        d.Handle(new SnapshotPublished(snapshot with { Version = 9 }), new SimTime(30_000), o);
        d.Handle(new AssignmentReleasedMsg(2, 0, AssignmentReason.Maintenance), new SimTime(30_000), o);

        Assert.Equal([1, 2], o.Messages.Select(m => m.Message).OfType<Assign>().Select(a => a.Request.Id));
    }
}
