using Elevator.Domain.Control;
using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

/// <summary>ElevatorHandler's accept/reject rules (ADR-006): maintenance and capacity always win; aging only blocks staleness.</summary>
public class AssignmentAcceptanceTests
{
    private sealed class FixedEta(long ms) : IEtaEstimator
    {
        public SimTime? EstimatePickup(ElevatorSnapshot snapshot, Request request, SimTime now) => new SimTime(ms);
    }

    private static Request R(int id, long calledAtMs = 0) => Request.Create(5, 9, new SimTime(calledAtMs), 10, id: id).Value!;

    private static (ElevatorHandler Handler, Outbox Outbox) Make(long ourEtaMs, int riders = 0, CapacityMode mode = CapacityMode.NextTrip)
    {
        var elevator = new Elevator(0, 10, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, 1, capacity: 5, bypassThreshold: 0.8, mode);
        for (var i = 0; i < riders; i++) elevator.Board(Request.Create(1, 2, SimTime.Zero, 10, id: 100 + i).Value!);
        return (new ElevatorHandler(elevator, new ElevatorHandlerOptions(StalenessToleranceMs: 10_000, AgingMs: 60_000), new FixedEta(ourEtaMs)), new Outbox());
    }

    private static AssignmentReason? Outcome(Outbox outbox) =>
        outbox.Events.OfType<AssignmentRejected>().SingleOrDefault()?.Reason;

    [Fact]
    public void Accepts_when_our_eta_is_within_tolerance()
    {
        var (h, o) = Make(ourEtaMs: 25_000);
        h.Handle(new Assign(R(1), new SimTime(16_000), Forced: false), SimTime.Zero, o);
        Assert.Null(Outcome(o));
        Assert.Contains(o.Events, e => e is AssignmentAccepted);
        Assert.Contains(o.Messages, m => m is { To.Kind: ActorKind.Passengers, Message: AssignmentAcceptedMsg });
    }

    [Fact]
    public void Rejects_as_stale_when_our_eta_is_much_worse()
    {
        var (h, o) = Make(ourEtaMs: 26_001);
        h.Handle(new Assign(R(1), new SimTime(16_000), Forced: false), SimTime.Zero, o);
        Assert.Equal(AssignmentReason.Stale, Outcome(o));
    }

    [Fact]
    public void Aged_or_forced_requests_cannot_be_rejected_as_stale()
    {
        var (h, o) = Make(ourEtaMs: 500_000);
        h.Handle(new Assign(R(1, calledAtMs: 0), new SimTime(70_000), Forced: false), new SimTime(60_000), o); // waited 60 s
        Assert.Null(Outcome(o));

        (h, o) = Make(ourEtaMs: 500_000);
        h.Handle(new Assign(R(2), new SimTime(16_000), Forced: true), SimTime.Zero, o);
        Assert.Null(Outcome(o));
    }

    [Fact]
    public void Next_trip_mode_a_full_elevator_accepts_for_a_later_trip_but_never_a_request_that_cannot_fit()
    {
        var (h, o) = Make(ourEtaMs: 0, riders: 4); // full: 4 ≥ 0.8 × 5
        h.Handle(new Assign(R(1), null, Forced: false), SimTime.Zero, o);
        Assert.Null(Outcome(o));

        (h, o) = Make(ourEtaMs: 0);
        var tooBig = Request.Create(5, 9, SimTime.Zero, 10, id: 2, load: 6).Value!; // load 6 > capacity 5
        h.Handle(new Assign(tooBig, null, Forced: true), new SimTime(120_000), o);
        Assert.Equal(AssignmentReason.Capacity, Outcome(o));
    }

    [Fact]
    public void Capacity_and_maintenance_rejects_always_win()
    {
        var (h, o) = Make(ourEtaMs: 0, riders: 4, mode: CapacityMode.Defer); // Defer: full now (4 ≥ 0.8 × 5) means no
        h.Handle(new Assign(R(1, calledAtMs: 0), null, Forced: true), new SimTime(120_000), o); // forced and aged
        Assert.Equal(AssignmentReason.Capacity, Outcome(o));

        (h, o) = Make(ourEtaMs: 0);
        h.Elevator.EnterMaintenance();
        h.Handle(new Assign(R(2), null, Forced: true), SimTime.Zero, o);
        Assert.Equal(AssignmentReason.Maintenance, Outcome(o));
    }
}
