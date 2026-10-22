using Elevator.Domain;
using Elevator.Policy.Dispatch;

namespace Elevator.Policy.Tests;

public class CostBasedDispatchTests
{
    private static readonly LookScheduling Look = new();

    private static Request Req(int id, int from, int to) => Request.Create(from, to, SimTime.Zero, 20, id: id).Value!;

    private static ElevatorSnapshot Car(int id, int floor, Direction? dir = null, int[]? carCalls = null, Request[]? riders = null, HallCall[]? pickups = null,
        ElevatorState state = ElevatorState.Idle) =>
        ElevatorSnapshot.ForPolicy(floor, state, dir, carCalls ?? [], pickups ?? [], 20, id) with
        {
            Capacity = 10,
            BypassThreshold = 0.8,
            Riders = riders ?? [],
        };

    private static CostBasedDispatch Cost(double lambda = 1.0, double mu = 10.0) => new(Look, ElevatorTiming.Default, new CostWeights(lambda, mu));

    private static DispatchContext Ctx(Request r, params ElevatorSnapshot[] cars) => new(r, cars, SimTime.Zero, ElevatorTiming.Default);

    [Fact]
    public void Idle_elevators_cost_is_the_pickup_eta()
    {
        var cost = Cost();
        var r = Req(1, 8, 12);
        Assert.Equal(15.0, cost.Evaluate(Car(0, 1), r, SimTime.Zero).Cost, 6);
        Assert.Equal(5.0, cost.Evaluate(Car(1, 10), r, SimTime.Zero).Cost, 6);
        Assert.Equal(1, cost.Choose(Ctx(r, Car(0, 1), Car(1, 10)))!.ElevatorId);
    }

    [Fact]
    public void Added_delay_to_riders_and_predicted_load_are_charged()
    {
        var a = Req(100, 5, 12);
        var e0 = Car(0, 5, Direction.Up, [12], [a]);
        var e1 = Car(1, 1);
        var r = Req(1, 8, 15);

        var eval = Cost().Evaluate(e0, r, SimTime.Zero);
        Assert.Equal((7.0, 5.0, 1), (eval.EtaS, eval.AddedDelayS, eval.LoadAtPickup));
        Assert.Equal(13.0, eval.Cost, 6);
        Assert.Equal(0, Cost().Choose(Ctx(r, e0, e1))!.ElevatorId);

        Assert.Equal(18.0, Cost(lambda: 2).Evaluate(e0, r, SimTime.Zero).Cost, 6);
        Assert.Equal(1, Cost(lambda: 2).Choose(Ctx(r, e0, e1))!.ElevatorId);
    }

    [Fact]
    public void A_snapshot_is_projected_forward_to_now_before_inserting_the_request()
    {
        var eval = Cost().Evaluate(Car(1, 10), Req(1, 8, 12), new SimTime(100_000));
        Assert.Equal((5.0, 105_000L), (eval.EtaS, eval.PickupAt!.Value.Ms));
    }

    [Fact]
    public void A_moving_elevator_snapshot_is_advanced_along_its_route()
    {
        var moving = Car(0, 1, Direction.Up, [15], [Req(100, 1, 15)], state: ElevatorState.MovingUp) with { PhaseEndsAt = new SimTime(2_000) };
        var eval = Cost().Evaluate(moving, Req(1, 8, 12), new SimTime(6_000));
        Assert.Equal((9.0, 15_000L), (eval.EtaS, eval.PickupAt!.Value.Ms));
    }

    [Fact]
    public void Next_trip_a_full_elevator_is_scored_with_its_next_trip_in_the_eta()
    {
        var riders = Enumerable.Range(100, 8).Select(i => Req(i, 1, 3)).ToArray();
        var e0 = Car(0, 1, Direction.Up, [3], riders);
        var e1 = Car(1, 20);
        var r = Req(1, 2, 9);

        var eval = Cost().Evaluate(e0, r, SimTime.Zero);
        Assert.Equal((12.0, 0.0, 0), (eval.EtaS, eval.AddedDelayS, eval.LoadAtPickup));
        Assert.Equal(0, Cost().Choose(Ctx(r, e0, e1))!.ElevatorId);
    }

    [Fact]
    public void Maintenance_elevators_are_ineligible_and_none_left_means_null()
    {
        var maintenance = Car(1, 5, state: ElevatorState.Maintenance);
        Assert.True(double.IsPositiveInfinity(Cost().Evaluate(maintenance, Req(1, 6, 9), SimTime.Zero).Cost));
        Assert.Null(Cost().Choose(Ctx(Req(1, 6, 9), maintenance)));
    }

    [Fact]
    public void Projection_reproduces_what_the_elevator_actually_does()
    {
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

    [Fact]
    public void Vip_priority_lets_a_vip_take_the_fastest_pickup_even_when_it_delays_riders()
    {
        var riders = Enumerable.Range(10, 5).Select(i => Req(i, 1, 15)).ToArray();
        var e0 = Car(0, 5, Direction.Up, carCalls: [15], riders: riders, state: ElevatorState.MovingUp) with { PhaseEndsAt = new SimTime(2_000) };
        var e1 = Car(1, 20);
        var regular = Req(1, 8, 9);
        var vip = regular with { Vip = true };
        var cost = Cost();

        Assert.Equal(1, cost.Choose(Ctx(regular, e0, e1) with { VipPriority = true })!.ElevatorId);
        Assert.Equal(0, cost.Choose(Ctx(vip, e0, e1) with { VipPriority = true })!.ElevatorId);
        Assert.Equal(1, cost.Choose(Ctx(vip, e0, e1))!.ElevatorId);
        var forVip = cost.Evaluate(e0, vip, SimTime.Zero, vipPriority: true);
        Assert.True(forVip.AddedDelayS > 0);
        Assert.Equal(forVip.EtaS + 10.0 * forVip.LoadAtPickup / 10, forVip.Cost, 6);
    }

    [Fact]
    public void Exact_cost_ties_go_to_the_lowest_id_whatever_the_candidate_order()
    {
        var r = Req(1, 8, 12);
        var (e2, e5) = (Car(2, 4), Car(5, 12));
        Assert.Equal(Cost().Evaluate(e2, r, SimTime.Zero).Cost, Cost().Evaluate(e5, r, SimTime.Zero).Cost, 9);
        Assert.Equal(2, Cost().Choose(Ctx(r, e2, e5))!.ElevatorId);
        Assert.Equal(2, Cost().Choose(Ctx(r, e5, e2))!.ElevatorId);
    }
}
