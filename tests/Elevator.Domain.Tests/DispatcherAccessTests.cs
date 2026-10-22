using Elevator.Domain.Control;
using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

public class DispatcherAccessTests
{
    private sealed class LowestId : IDispatchPolicy
    {
        public string Name => "lowest";

        public DispatchChoice? Choose(DispatchContext c)
        {
            var best = c.Candidates.OrderBy(s => s.ElevatorId).FirstOrDefault();
            if (best is null) return null;
            return new DispatchChoice(best.ElevatorId, best.ElevatorId)
            {
                Candidates = c.Explain ? [.. c.Candidates.OrderBy(s => s.ElevatorId).Select(s => new CandidateCost(s.ElevatorId, s.ElevatorId, "id"))] : null,
            };
        }
    }

    private sealed class Refuse(string name, int[] elevators, int? floor = null) : IAccessPolicy
    {
        public string Name => name;

        public string? Deny(Request request, ElevatorSnapshot elevator) =>
            elevators.Contains(elevator.ElevatorId) && (floor is null || request.DestinationFloor == floor) ? $"{name} says no to E{elevator.ElevatorId}" : null;
    }

    private static Request R(int id, int to = 7, bool vip = false, long at = 0) =>
        Request.Create(3, to, new SimTime(at), 10, id: id).Value! with { Vip = vip };

    private static (DispatcherHandler D, Outbox O) Make(AccessPipeline access, int elevators = 3, bool explain = false, bool publish = true)
    {
        var d = new DispatcherHandler(new LowestId(), elevators, ElevatorTiming.Default, new DispatcherOptions(EmitDispatchEvaluations: explain) { Access = access });
        var o = new Outbox();
        if (publish) Publish(d, o, elevators);
        return (d, o);
    }

    private static void Publish(DispatcherHandler d, Outbox o, int elevators)
    {
        for (var i = 0; i < elevators; i++)
            d.Handle(new SnapshotPublished(new Elevator(i, 10, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall).Snapshot()), SimTime.Zero, o);
    }

    private static IEnumerable<int> ProposedTo(Outbox o) => o.Events.OfType<AssignmentProposed>().Select(p => p.ElevatorId);

    [Fact]
    public void A_denied_elevator_is_never_proposed_and_the_denial_is_logged_once()
    {
        var (d, o) = Make(new AccessPipeline([new Refuse("stub", [0])]));
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        Assert.Equal([1], ProposedTo(o));
        d.Handle(new AssignmentRejectedMsg(1, 1, AssignmentReason.Stale), SimTime.Zero, o);
        Assert.Equal([1, 2], ProposedTo(o));

        var denied = Assert.Single(o.Events.OfType<AccessDenied>());
        Assert.Equal(new AccessDenied(SimTime.Zero, 1, 0, "stub", "stub says no to E0"), denied);
    }

    [Fact]
    public void Forced_assignments_also_respect_access()
    {
        var (d, o) = Make(new AccessPipeline([new Refuse("stub", [0])]));
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        d.Handle(new AssignmentRejectedMsg(1, 1, AssignmentReason.Stale), SimTime.Zero, o);
        d.Handle(new AssignmentRejectedMsg(1, 2, AssignmentReason.Stale), SimTime.Zero, o);
        var forced = o.Events.OfType<AssignmentProposed>().Last();
        Assert.True(forced.Forced);
        Assert.NotEqual(0, forced.ElevatorId);
    }

    [Fact]
    public void Denied_by_every_elevator_the_request_is_refused_gracefully()
    {
        var (d, o) = Make(new AccessPipeline([new Refuse("restricted", [0, 1, 2], floor: 9)]));
        d.Handle(new SubmitRequest(R(1, to: 9)), SimTime.Zero, o);

        Assert.Empty(o.Events.OfType<AssignmentProposed>());
        Assert.Equal([0, 1, 2, null], o.Events.OfType<AccessDenied>().Select(a => a.ElevatorId));
        Assert.Equal(new AccessDenied(SimTime.Zero, 1, null, "restricted", "restricted says no to E0"), o.Events.OfType<AccessDenied>().Last());
        Assert.Contains(o.Messages, m => m is { To.Kind: ActorKind.Passengers, Message: RequestRefused { RequestId: 1 } });
        Assert.Contains(o.Messages, m => m is { To.Kind: ActorKind.Client, Message: RequestRefused { RequestId: 1, Policy: "restricted" } });
        Assert.Equal(0, d.InFlight);

        o.Clear();
        d.Handle(new SubmitRequest(R(2, to: 8)), SimTime.Zero, o);
        Assert.Equal([0], ProposedTo(o));
        Assert.Empty(o.Events.OfType<AccessDenied>());
    }

    [Fact]
    public void Explanations_list_denied_elevators_with_no_cost_and_the_policy_as_note()
    {
        var (d, o) = Make(new AccessPipeline([new Refuse("served-floors", [1])]), explain: true);
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        var evaluated = Assert.Single(o.Events.OfType<DispatchEvaluated>());
        Assert.Equal([new CandidateCost(0, 0, "id"), new CandidateCost(1, null, "served-floors"), new CandidateCost(2, 2, "id")], evaluated.Candidates);
        Assert.Equal(0, evaluated.WinnerId);
    }

    [Fact]
    public void Pending_requests_are_served_by_call_time_and_vip_status_does_not_reorder_them()
    {
        var d = new DispatcherHandler(new LowestId(), 3, ElevatorTiming.Default, new DispatcherOptions { VipPriority = true });
        var o = new Outbox();
        d.Handle(new SubmitRequest(R(1, at: 0)), new SimTime(10), o);
        d.Handle(new SubmitRequest(R(2, vip: true, at: 5)), new SimTime(10), o);
        d.Handle(new SubmitRequest(R(3, at: 1)), new SimTime(10), o);
        Publish(d, o, 3);
        Assert.Equal([1, 3, 2], o.Events.OfType<AssignmentProposed>().Select(p => p.RequestId));
    }

    [Fact]
    public void Refused_passengers_leave_and_are_counted()
    {
        var p = new PassengersHandler(10);
        var o = new Outbox();
        p.Handle(new PassengerArrives(R(1, to: 9)), SimTime.Zero, o);
        Assert.Equal(1, p.WaitingCounts[(3, Direction.Up)]);
        p.Handle(new RequestRefused(1, "restricted", "no"), SimTime.Zero, o);
        Assert.Empty(p.WaitingCounts);
        Assert.Equal((1, 0, true), (p.Refused, p.Delivered, p.Empty));
    }
}
