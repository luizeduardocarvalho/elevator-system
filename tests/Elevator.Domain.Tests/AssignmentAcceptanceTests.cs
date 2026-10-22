using Elevator.Domain.Control;
using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

public class AssignmentAcceptanceTests
{
    private sealed class FixedEta(long ms) : IEtaEstimator
    {
        public Instant? EstimatePickup(ElevatorSnapshot snapshot, Request request, Instant now) => new Instant(ms);
    }

    private static Request R(int id, long calledAtMs = 0) => Request.Create(5, 9, new Instant(calledAtMs), 10, id: id).Value!;

    private static (ElevatorController Handler, Outbox Outbox) Make(long ourEtaMs, int riders = 0)
    {
        var elevator = new Elevator(0, 10, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, 1, capacity: 5, bypassThreshold: 0.8);
        for (var i = 0; i < riders; i++) elevator.Board(Request.Create(1, 2, Instant.Zero, 10, id: 100 + i).Value!);
        return (new ElevatorController(elevator, new ElevatorControllerOptions(StalenessToleranceMs: 10_000, AgingMs: 60_000), new FixedEta(ourEtaMs)), new Outbox());
    }

    private static AssignmentReason? Outcome(Outbox outbox) =>
        outbox.Events.OfType<AssignmentRejected>().SingleOrDefault()?.Reason;

    [Fact]
    public void Accepts_when_our_eta_is_within_tolerance()
    {
        var (h, o) = Make(ourEtaMs: 25_000);
        h.Handle(new Assign(R(1), new Instant(16_000), Forced: false), Instant.Zero, o);
        Assert.Null(Outcome(o));
        Assert.Contains(o.Events, e => e is AssignmentAccepted);
        Assert.Contains(o.Messages, m => m is { To.Kind: ActorKind.Passengers, Message: AssignmentAcceptedMsg });
    }

    [Fact]
    public void Rejects_as_stale_when_our_eta_is_much_worse()
    {
        var (h, o) = Make(ourEtaMs: 26_001);
        h.Handle(new Assign(R(1), new Instant(16_000), Forced: false), Instant.Zero, o);
        Assert.Equal(AssignmentReason.Stale, Outcome(o));
    }

    [Fact]
    public void Aged_or_forced_requests_cannot_be_rejected_as_stale()
    {
        var (h, o) = Make(ourEtaMs: 500_000);
        h.Handle(new Assign(R(1, calledAtMs: 0), new Instant(70_000), Forced: false), new Instant(60_000), o);
        Assert.Null(Outcome(o));

        (h, o) = Make(ourEtaMs: 500_000);
        h.Handle(new Assign(R(2), new Instant(16_000), Forced: true), Instant.Zero, o);
        Assert.Null(Outcome(o));
    }

    [Fact]
    public void Next_trip_mode_a_full_elevator_accepts_for_a_later_trip_but_never_a_request_that_cannot_fit()
    {
        var (h, o) = Make(ourEtaMs: 0, riders: 4);
        h.Handle(new Assign(R(1), null, Forced: false), Instant.Zero, o);
        Assert.Null(Outcome(o));

        (h, o) = Make(ourEtaMs: 0);
        var tooBig = Request.Create(5, 9, Instant.Zero, 10, id: 2, load: 6).Value!;
        h.Handle(new Assign(tooBig, null, Forced: true), new Instant(120_000), o);
        Assert.Equal(AssignmentReason.Capacity, Outcome(o));
    }

    [Fact]
    public void Capacity_and_maintenance_rejects_always_win()
    {
        var (h, o) = Make(ourEtaMs: 0, riders: 4);
        var neverFits = Request.Create(5, 9, Instant.Zero, 10, id: 1, load: 6).Value!;
        h.Handle(new Assign(neverFits, null, Forced: true), new Instant(120_000), o);
        Assert.Equal(AssignmentReason.Capacity, Outcome(o));

        (h, o) = Make(ourEtaMs: 0);
        h.Elevator.EnterMaintenance();
        h.Handle(new Assign(R(2), null, Forced: true), Instant.Zero, o);
        Assert.Equal(AssignmentReason.Maintenance, Outcome(o));
    }
}
