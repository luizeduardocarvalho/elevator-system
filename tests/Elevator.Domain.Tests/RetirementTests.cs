using Elevator.Domain.Control;
using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

/// <summary>
/// <see cref="PassengerRetired"/> (perf only, ADR-015): the steady-state throughput ramp retires each request as soon as
/// it is accepted, so the building keeps its seeded backlog instead of accumulating the generator's requests. The
/// handlers must then be exactly as if the request had never existed.
/// </summary>
public class RetirementTests
{
    private sealed class First : IDispatchPolicy
    {
        public string Name => "first";

        public DispatchChoice? Choose(DispatchContext c) =>
            c.Candidates.Select(s => new DispatchChoice(s.ElevatorId, 0, c.Now)).FirstOrDefault();
    }

    private static Request R(int id, int from = 3, int to = 7) => Request.Create(from, to, SimTime.Zero, 10, id: id).Value!;

    private static ElevatorHandler Elevator0() => new(new Elevator(0, 10, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall));

    private static void Accept(ElevatorHandler h, Request r, Outbox o) => h.Handle(new Assign(r, null, Forced: false), SimTime.Zero, o);

    [Fact]
    public void Retiring_an_accepted_pickup_drops_it_without_releasing_it_and_publishes_a_snapshot()
    {
        var h = Elevator0();
        var o = new Outbox();
        h.Handle(new Start(), SimTime.Zero, o);
        Accept(h, R(1), o);
        Assert.Contains(h.Elevator.Snapshot().HallCalls, c => c.Requests.Any(r => r.Id == 1));
        o.Clear();

        h.Handle(new PassengerRetired(1, 0), SimTime.Zero, o);

        Assert.DoesNotContain(h.Elevator.Snapshot().HallCalls, c => c.Requests.Any(r => r.Id == 1));
        Assert.Contains(o.Messages, m => m.Message is SnapshotPublished);
        Assert.DoesNotContain(o.Messages, m => m.Message is AssignmentReleasedMsg); // a release would be re-dispatched
        Assert.DoesNotContain(o.Events, e => e is AssignmentReleased);
    }

    [Fact]
    public void Assign_then_retire_leaves_the_elevator_as_if_the_request_never_existed()
    {
        var withRequest = Elevator0();
        var without = Elevator0();
        var o = new Outbox();
        foreach (var h in new[] { withRequest, without }) { h.Handle(new Start(), SimTime.Zero, o); Accept(h, R(1, 2, 9), o); }

        Accept(withRequest, R(2), o);
        withRequest.Handle(new PassengerRetired(2, 0), SimTime.Zero, o);

        var (a, b) = (withRequest.Elevator.Snapshot(), without.Elevator.Snapshot());
        Assert.Equal(b.HallCalls.SelectMany(c => c.Requests), a.HallCalls.SelectMany(c => c.Requests));
        Assert.Equal((b.State, b.CurrentFloor, b.CommittedDirection, b.Riders.Count), (a.State, a.CurrentFloor, a.CommittedDirection, a.Riders.Count));
    }

    [Fact]
    public void Retiring_an_unknown_request_changes_nothing()
    {
        var h = Elevator0();
        var o = new Outbox();
        h.Handle(new Start(), SimTime.Zero, o);
        var version = h.Elevator.Version;
        o.Clear();

        h.Handle(new PassengerRetired(42, 0), SimTime.Zero, o);

        Assert.Equal(version, h.Elevator.Version);
        Assert.DoesNotContain(o.Messages, m => m.Message is SnapshotPublished);
    }

    [Fact]
    public void A_retired_passenger_stops_waiting_and_the_dispatcher_forgets_the_request()
    {
        var p = new PassengersHandler(10);
        var o = new Outbox();
        p.Handle(new PassengerArrives(R(1)), SimTime.Zero, o);
        p.Handle(new AssignmentAcceptedMsg(1, 0), SimTime.Zero, o);
        Assert.Equal(1, p.WaitingCounts[(3, Direction.Up)]);
        o.Clear();

        p.Handle(new PassengerRetired(1, 0), SimTime.Zero, o);

        Assert.Empty(p.WaitingCounts);
        Assert.True(p.Empty);
        Assert.Equal(1, p.Retired);
        // The dispatcher forgets it through the message it already handles when a passenger boards.
        Assert.Contains(o.Messages, m => m is { To.Kind: ActorKind.Dispatcher, Message: RequestPickedUp { RequestId: 1, ElevatorId: 0 } });
        Assert.Empty(o.Events); // nothing in the log: the passenger never boarded or alighted
    }

    [Fact]
    public void A_passenger_already_riding_is_not_retired()
    {
        var p = new PassengersHandler(10);
        var o = new Outbox();
        p.Handle(new PassengerArrives(R(1)), SimTime.Zero, o);
        p.Handle(new AssignmentAcceptedMsg(1, 0), SimTime.Zero, o);
        p.Handle(new ElevatorNotice(new HallCallAnswered(SimTime.Zero, 0, 3, Direction.Up)), SimTime.Zero, o);
        o.Clear();

        p.Handle(new PassengerRetired(1, 0), SimTime.Zero, o);

        Assert.Equal(0, p.Retired);
        Assert.Empty(o.Messages);
    }

    [Fact]
    public void The_dispatcher_forgets_a_retired_request_and_its_in_flight_count_drops()
    {
        var d = new DispatcherHandler(new First(), 1, ElevatorTiming.Default);
        var o = new Outbox();
        d.Handle(new SnapshotPublished(new Elevator(0, 10, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall).Snapshot()), SimTime.Zero, o);
        d.Handle(new SubmitRequest(R(1)), SimTime.Zero, o);
        d.Handle(new AssignmentAcceptedMsg(1, 0, 1), SimTime.Zero, o);
        Assert.Equal(1, d.InFlight);

        d.Handle(new RequestPickedUp(1, 0), SimTime.Zero, o);

        Assert.Equal(0, d.InFlight);
    }
}
