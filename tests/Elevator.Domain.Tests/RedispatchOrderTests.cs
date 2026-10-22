using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Tests;

public class RedispatchOrderTests
{
    private static Request R(int id, int from, long calledAtMs) => Request.Create(from, from + 2, new SimTime(calledAtMs), 12, id: id).Value!;

    [Fact]
    public void A_hand_back_batch_is_emitted_oldest_call_first_whatever_the_stop_order()
    {
        var h = new ElevatorHandler(new Elevator(0, 12, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall));
        var o = new Outbox();
        h.Handle(new Start(), SimTime.Zero, o);
        h.Handle(new Assign(R(3, 9, calledAtMs: 30_000), null, Forced: false), new SimTime(40_000), o);
        h.Handle(new Assign(R(1, 5, calledAtMs: 10_000), null, Forced: false), new SimTime(40_000), o);
        h.Handle(new Assign(R(2, 7, calledAtMs: 20_000), null, Forced: false), new SimTime(40_000), o);
        o.Clear();

        h.Handle(new ApplyFault(new FaultCommand(new SimTime(40_000), 0, FaultKind.MaintenanceOn, null)), new SimTime(40_000), o);

        Assert.Equal([1, 2, 3], o.Events.OfType<AssignmentReleased>().Select(e => e.RequestId));
        Assert.Equal([1, 2, 3], o.Messages.Where(m => m.To.Kind == ActorKind.Dispatcher).Select(m => m.Message)
            .OfType<AssignmentReleasedMsg>().Select(m => m.RequestId));
    }

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

        d.Handle(new SubmitRequest(R(2, 7, calledAtMs: 20_000)), new SimTime(20_000), o);
        d.Handle(new AssignmentAcceptedMsg(2, 0, 1), new SimTime(20_000), o);
        d.Handle(new SubmitRequest(R(1, 5, calledAtMs: 10_000)), new SimTime(21_000), o);
        d.Handle(new AssignmentRejectedMsg(1, 0, AssignmentReason.Capacity), new SimTime(21_000), o);
        o.Clear();

        d.Handle(new SnapshotPublished(snapshot with { Version = 9 }), new SimTime(30_000), o);
        d.Handle(new AssignmentReleasedMsg(2, 0, AssignmentReason.Maintenance), new SimTime(30_000), o);

        Assert.Equal([1, 2], o.Messages.Select(m => m.Message).OfType<Assign>().Select(a => a.Request.Id));
    }
}
