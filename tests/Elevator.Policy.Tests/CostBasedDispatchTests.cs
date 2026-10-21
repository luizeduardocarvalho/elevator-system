using Elevator.Domain;
using Elevator.Policy.Dispatch;

namespace Elevator.Policy.Tests;

/// <summary>
/// Hand-computed examples. Timing: 2 s per floor; doors 1 s opening, 3 s open, 1 s closing.
/// A pickup happens when the doors are fully open (arrival + 1 s). cost = ETA + λ·ΔD + μ·L̂/C.
/// </summary>
public class CostBasedDispatchTests
{
    private static readonly LookScheduling Look = new();

    private static Request Req(int id, int from, int to) => Request.Create(from, to, SimTime.Zero, 20, id: id).Value!;

    private static ElevatorSnapshot Car(int id, int floor, Direction? dir = null, int[]? carCalls = null, Request[]? riders = null, HallCall[]? pickups = null,
        ElevatorState state = ElevatorState.Idle, CapacityMode mode = CapacityMode.NextTrip) =>
        ElevatorSnapshot.ForPolicy(floor, state, dir, carCalls ?? [], pickups ?? [], 20, id) with
        {
            Capacity = 10,
            BypassThreshold = 0.8,
            Riders = riders ?? [],
            CapacityMode = mode,
        };

    private static CostBasedDispatch Cost(double lambda = 1.0, double mu = 10.0) => new(Look, ElevatorTiming.Default, new CostWeights(lambda, mu));

    private static DispatchContext Ctx(Request r, params ElevatorSnapshot[] cars) => new(r, cars, SimTime.Zero, ElevatorTiming.Default, 0);

    [Fact]
    public void Idle_elevators_cost_is_the_pickup_eta()
    {
        // Request 8→12. E0 idle at 1: 7 floors = 14 s, doors open at 15 → cost 15.
        //                E1 idle at 10: 2 floors = 4 s, doors open at 5   → cost 5. E1 wins.
        var cost = Cost();
        var r = Req(1, 8, 12);
        Assert.Equal(15.0, cost.Evaluate(Car(0, 1), r, SimTime.Zero).Cost, 6);
        Assert.Equal(5.0, cost.Evaluate(Car(1, 10), r, SimTime.Zero).Cost, 6);
        Assert.Equal(1, cost.Choose(Ctx(r, Car(0, 1), Car(1, 10)))!.ElevatorId);
    }

    [Fact]
    public void Added_delay_to_riders_and_predicted_load_are_charged()
    {
        // E0 at 5 going up with rider A (5→12). New request R 8→15.
        //   Without R: 5→12 is 7 floors = 14 s, doors open at 15 → A arrives at 15.
        //   With R (LOOK stops at 8 on the way): 3 floors = 6 s, open at 7 → ETA 7. Dwell to 10, closed 11,
        //   8→12 is 4 floors → 19, open at 20 → A arrives at 20: ΔD = 5 s. Load at pickup L̂ = 1 (A).
        //   cost = 7 + 1.0·5 + 10·1/10 = 13.
        // E1 idle at 1: 7 floors = 14 s, open at 15 → cost 15. E0 wins.
        var a = Req(100, 5, 12);
        var e0 = Car(0, 5, Direction.Up, [12], [a]);
        var e1 = Car(1, 1);
        var r = Req(1, 8, 15);

        var eval = Cost().Evaluate(e0, r, SimTime.Zero);
        Assert.Equal((7.0, 5.0, 1), (eval.EtaS, eval.AddedDelayS, eval.LoadAtPickup));
        Assert.Equal(13.0, eval.Cost, 6);
        Assert.Equal(0, Cost().Choose(Ctx(r, e0, e1))!.ElevatorId);

        // Weighting existing riders more (λ = 2) flips the decision: E0 = 7 + 10 + 1 = 18 > 15.
        Assert.Equal(18.0, Cost(lambda: 2).Evaluate(e0, r, SimTime.Zero).Cost, 6);
        Assert.Equal(1, Cost(lambda: 2).Choose(Ctx(r, e0, e1))!.ElevatorId);
    }

    [Fact]
    public void A_snapshot_is_projected_forward_to_now_before_inserting_the_request()
    {
        // E1 has been idle at 10 since t=0 (its last snapshot). The dispatcher decides at t=100 s: the elevator can only
        // start moving then, so the pickup is at 100 + 4 + 1 = 105 s and the ETA is 5 s. (Projecting from the
        // snapshot's own time would claim a pickup at 5 s, i.e. an ETA of 0: the bug the M3 bench exposed.)
        var eval = Cost().Evaluate(Car(1, 10), Req(1, 8, 12), new SimTime(100_000));
        Assert.Equal((5.0, 105_000L), (eval.EtaS, eval.PickupAt!.Value.Ms));
    }

    [Fact]
    public void A_moving_elevator_snapshot_is_advanced_along_its_route()
    {
        // E0 left floor 1 at t=0 towards a drop-off at 15 (snapshot at t=0, mid-phase). At t=6 s it is at floor 4;
        // a pickup at 8 (up) is reached at 14 s (7 floors × 2 s), doors open at 15 → ETA 9 s from now.
        var moving = Car(0, 1, Direction.Up, [15], [Req(100, 1, 15)], state: ElevatorState.MovingUp) with { PhaseEndsAt = new SimTime(2_000) };
        var eval = Cost().Evaluate(moving, Req(1, 8, 12), new SimTime(6_000));
        Assert.Equal((9.0, 15_000L), (eval.EtaS, eval.PickupAt!.Value.Ms));
    }

    [Fact]
    public void Next_trip_a_full_elevator_is_scored_with_its_next_trip_in_the_eta()
    {
        // E0 at 1 carries 8 riders to 3 (full: 8 ≥ 0.8 × 10). R: 2 → 9.
        //   It bypasses 2 (full), reaches 3 at 4 s, doors open at 5 s, riders get off: room again.
        //   Lantern off (no work was visible while full); doors close 5–9; then the nearest stop is R's pickup at 2:
        //   down one floor, 9–11 s, doors open at 12 s → ETA 12 s. ΔD 0 (riders finish at 5 s either way); L̂ 0.
        // E1 idle at 20: 18 floors (36 s) + opening → 37 s.
        var riders = Enumerable.Range(100, 8).Select(i => Req(i, 1, 3)).ToArray();
        var e0 = Car(0, 1, Direction.Up, [3], riders);
        var e1 = Car(1, 20);
        var r = Req(1, 2, 9);

        var eval = Cost().Evaluate(e0, r, SimTime.Zero);
        Assert.Equal((12.0, 0.0, 0), (eval.EtaS, eval.AddedDelayS, eval.LoadAtPickup));
        Assert.Equal(0, Cost().Choose(Ctx(r, e0, e1))!.ElevatorId); // next trip of E0 (12 s) beats E1 (37 s)

        // In Defer mode a full elevator cannot be chosen: only E1.
        Assert.Equal(1, Cost().Choose(Ctx(r, e0 with { CapacityMode = CapacityMode.Defer }, e1 with { CapacityMode = CapacityMode.Defer }))!.ElevatorId);
    }

    [Fact]
    public void Defer_predicted_load_at_pickup_makes_an_elevator_ineligible()
    {
        // Threshold 0.8 × 10 = 8. E0 at 1 carries 6 riders to 15 and has 2 more people to pick up at 3 (up):
        // they board at 3, the load reaches 8 = threshold, so it bypasses and hands R back: predicted load 8 ≥ 8
        // → ineligible (∞), although it is the closest elevator.
        var riders = Enumerable.Range(100, 6).Select(i => Req(i, 1, 15)).ToArray();
        var pickups = new[] { new HallCall(3, Direction.Up, SimTime.Zero, [Req(200, 3, 9), Req(201, 3, 10)]) };
        var e0 = Car(0, 1, Direction.Up, [15], riders, pickups, mode: CapacityMode.Defer);
        var r = Req(1, 5, 12);

        var eval = Cost().Evaluate(e0, r, SimTime.Zero);
        Assert.Equal(8, eval.LoadAtPickup);
        Assert.True(double.IsPositiveInfinity(eval.Cost));
        Assert.Equal(1, Cost().Choose(Ctx(r, e0, Car(1, 20, mode: CapacityMode.Defer)))!.ElevatorId);
    }

    [Fact]
    public void Defer_full_or_maintenance_elevators_are_ineligible_and_none_left_means_null()
    {
        var full = Car(0, 5, riders: Enumerable.Range(100, 8).Select(i => Req(i, 5, 15)).ToArray(), mode: CapacityMode.Defer);
        var maintenance = Car(1, 5, state: ElevatorState.Maintenance, mode: CapacityMode.Defer);
        Assert.True(double.IsPositiveInfinity(Cost().Evaluate(full, Req(1, 6, 9), SimTime.Zero).Cost));
        Assert.True(double.IsPositiveInfinity(Cost().Evaluate(maintenance, Req(1, 6, 9), SimTime.Zero).Cost));
        Assert.Null(Cost().Choose(Ctx(Req(1, 6, 9), full, maintenance)));
    }

    [Fact]
    public void Projection_reproduces_what_the_elevator_actually_does()
    {
        // The projected pickup time equals the time a real elevator answers, because the projection runs the same code.
        var e = new Elevator.Domain.Elevator(0, 20, ElevatorTiming.Default, Look, 1, 10, 0.8);
        e.AssignPickup(Req(1, 9, 3));
        e.Tick(new SimTime(5_300));
        var r = Req(2, 6, 14);
        var projected = new RouteProjection(Look, ElevatorTiming.Default).EstimatePickup(e.Snapshot(), r, new SimTime(5_300));

        e.AssignPickup(r);
        e.DrainEvents();
        SimTime? actual = null;
        for (var t = 5_300L; actual is null && t < 200_000; t += 100)
        {
            e.Tick(new SimTime(t));
            foreach (var ev in e.DrainEvents())
            {
                if (ev is Domain.Events.HallCallAnswered { Floor: 9 }) e.Board(Req(1, 9, 3));
                if (ev is Domain.Events.HallCallAnswered { Floor: 6 }) actual = ev.At;
            }
        }
        Assert.Equal(actual, projected);
    }

    [Fact]
    public void Cost_dispatch_is_registered() =>
        Assert.IsType<CostBasedDispatch>(DispatchPolicies.Create("cost", Look, ElevatorTiming.Default));
}
