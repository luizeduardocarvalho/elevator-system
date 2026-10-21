using Elevator.Domain.Control;
using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

public class DispatcherProtocolTests
{
    /// <summary>Picks the lowest-id candidate; ETA 1 s from now so elevators can be "stale".</summary>
    private sealed class LowestId : IDispatchPolicy
    {
        public string Name => "lowest";

        public DispatchChoice? Choose(DispatchContext c) =>
            c.Candidates.OrderBy(s => s.ElevatorId).Select(s => new DispatchChoice(s.ElevatorId, s.ElevatorId, c.Now.AddMs(1_000))).FirstOrDefault();
    }

    private static Request R(int id) => Request.Create(3, 7, SimTime.Zero, 10, id: id).Value!;

    private static (DispatcherHandler D, Outbox O) Make(int elevators = 2)
    {
        var d = new DispatcherHandler(new LowestId(), elevators, ElevatorTiming.Default);
        var o = new Outbox();
        for (var i = 0; i < elevators; i++)
        {
            var snapshot = new Elevator(i, 10, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall).Snapshot();
            d.Handle(new SnapshotPublished(snapshot), SimTime.Zero, o);
        }
        o.Clear();
        return (d, o);
    }

    private static Assign LastAssign(Outbox o, out int elevator)
    {
        var env = o.Messages.Last(m => m.Message is Assign);
        elevator = env.To.Index;
        return (Assign)env.Message;
    }

    [Fact]
    public void Stale_rejections_from_everyone_lead_to_a_forced_assignment()
    {
        var (d, o) = Make();
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        Assert.False(LastAssign(o, out var first).Forced);
        Assert.Equal(0, first);

        d.Handle(new AssignmentRejectedMsg(1, 0, AssignmentReason.Stale), SimTime.Zero, o);
        Assert.Equal((1, false), (Elevator(LastAssign(o, out var second), second), LastAssign(o, out _).Forced));

        d.Handle(new AssignmentRejectedMsg(1, 1, AssignmentReason.Stale), SimTime.Zero, o);
        var forced = LastAssign(o, out var third);
        Assert.True(forced.Forced);
        Assert.Equal(0, third);
        Assert.Contains(o.Events, e => e is AssignmentProposed { Forced: true });
    }

    [Fact]
    public void Capacity_rejections_never_force_the_request_waits_instead()
    {
        var (d, o) = Make();
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        d.Handle(new AssignmentRejectedMsg(1, 0, AssignmentReason.Capacity), SimTime.Zero, o);
        d.Handle(new AssignmentRejectedMsg(1, 1, AssignmentReason.Stale), SimTime.Zero, o);

        Assert.DoesNotContain(o.Events, e => e is AssignmentProposed { Forced: true });
        Assert.Single(o.Events.OfType<DispatchDeferred>());
    }

    [Fact]
    public void A_new_snapshot_makes_a_rejecting_elevator_eligible_again()
    {
        var (d, o) = Make(elevators: 1);
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        d.Handle(new AssignmentRejectedMsg(1, 0, AssignmentReason.Capacity), SimTime.Zero, o);
        Assert.Single(o.Events.OfType<DispatchDeferred>());

        o.Clear();
        var snapshot = new Elevator(0, 10, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall).Snapshot();
        d.Handle(new SnapshotPublished(snapshot), new SimTime(5_000), o);
        Assert.Single(o.Messages, m => m.Message is Assign);
    }

    [Fact]
    public void Submissions_are_idempotent_while_in_flight()
    {
        var (d, o) = Make();
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);

        Assert.Single(o.Events.OfType<RequestSubmitted>());
        Assert.Single(o.Events.OfType<DuplicateSubmission>());
        Assert.Single(o.Messages, m => m.Message is Assign);
    }

    [Fact]
    public void Acceptance_is_acknowledged_to_the_client_and_pickup_ends_tracking()
    {
        var (d, o) = Make();
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        d.Handle(new AssignmentAcceptedMsg(1, 0), SimTime.Zero, o);
        Assert.Contains(o.Messages, m => m is { To.Kind: ActorKind.Client, Message: RequestAssigned { RequestId: 1, ElevatorId: 0 } });
        Assert.Equal(1, d.InFlight);

        d.Handle(new RequestPickedUp(1, 0), SimTime.Zero, o);
        Assert.Equal(0, d.InFlight);
    }

    [Fact]
    public void Re_registration_keeps_the_original_request()
    {
        var (d, o) = Make();
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        d.Handle(new AssignmentAcceptedMsg(1, 0), SimTime.Zero, o);
        o.Clear();

        d.Handle(new ReRegister(1, AssignmentReason.LeftBehind), new SimTime(40_000), o);
        var again = LastAssign(o, out _);
        Assert.Equal((1, 0L), (again.Request.Id, again.Request.Timestamp.Ms)); // same id, same t_call
        Assert.Empty(o.Events.OfType<RequestSubmitted>());
    }

    private static int Elevator(Assign _, int elevator) => elevator;
}
