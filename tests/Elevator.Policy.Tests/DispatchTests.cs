using Elevator.Domain;
using Elevator.Policy.Dispatch;

namespace Elevator.Policy.Tests;

public class DispatchTests
{
    private static Request Req(int from, int to, int id = 1) => Request.Create(from, to, SimTime.Zero, 20, id: id).Value!;

    private static ElevatorSnapshot Car(int id, int floor, Direction? dir = null, int[]? carCalls = null, ElevatorState state = ElevatorState.Idle, int load = 0) =>
        ElevatorSnapshot.ForPolicy(floor, state, dir, carCalls ?? [], [], 20, id) with
        {
            Capacity = 10,
            BypassThreshold = 0.8,
            Riders = Enumerable.Range(100, load).Select(i => Req(1, 2, i)).ToArray(),
        };

    private static DispatchContext Ctx(Request r, params ElevatorSnapshot[] cars) =>
        new(r, cars, SimTime.Zero, ElevatorTiming.Default);

    [Fact]
    public void Nearest_idle_elevator_wins() =>
        Assert.Equal(1, new NearestDispatch().Choose(Ctx(Req(8, 1), Car(0, 1), Car(1, 10)))!.ElevatorId);

    [Fact]
    public void Same_direction_ahead_counts_plain_distance()
    {
        // E0 at 3 going up to 12; request at 8 going up: on the way, 5 floors.
        var e0 = Car(0, 3, Direction.Up, [12], ElevatorState.MovingUp);
        Assert.Equal(5, NearestDispatch.EffectiveDistance(e0, Req(8, 15)));
    }

    [Fact]
    public void Opposite_direction_must_finish_the_sweep_first()
    {
        // E0 at 3 going up to 12; request at 8 going down: up to 12 (9 floors) then back down to 8 (4) = 13.
        var e0 = Car(0, 3, Direction.Up, [12], ElevatorState.MovingUp);
        Assert.Equal(13, NearestDispatch.EffectiveDistance(e0, Req(8, 1)));
        // An idle elevator at 14 (distance 6) therefore beats it.
        Assert.Equal(1, new NearestDispatch().Choose(Ctx(Req(8, 1), e0, Car(1, 14)))!.ElevatorId);
    }

    [Fact]
    public void Behind_a_moving_elevator_counts_the_sweep_and_back()
    {
        // E0 at 10 going up to 15; request at 6 going up: 5 up + 9 back down = 14.
        Assert.Equal(14, NearestDispatch.EffectiveDistance(Car(0, 10, Direction.Up, [15], ElevatorState.MovingUp), Req(6, 9)));
    }

    [Fact]
    public void Ties_go_to_the_lowest_id() =>
        Assert.Equal(0, new NearestDispatch().Choose(Ctx(Req(5, 9), Car(1, 3), Car(0, 7)))!.ElevatorId);

    [Fact]
    public void Elevators_with_room_are_preferred_and_maintenance_is_never_chosen()
    {
        var full = Car(0, 5, load: 8);           // 8 ≥ 0.8 × 10
        var maintenance = Car(1, 5, state: ElevatorState.Maintenance);
        var far = Car(2, 20);
        Assert.Equal(2, new NearestDispatch().Choose(Ctx(Req(5, 9), full, maintenance, far))!.ElevatorId);
    }

    [Fact]
    public void Next_trip_when_every_elevator_is_full_one_is_still_named()
    {
        var full = Car(0, 5, load: 8);
        var maintenance = Car(1, 5, state: ElevatorState.Maintenance);
        Assert.Equal(0, new NearestDispatch().Choose(Ctx(Req(5, 9), full, maintenance))!.ElevatorId);
    }

    [Fact]
    public void Dispatch_policies_are_registered_by_name()
    {
        Assert.IsType<NearestDispatch>(DispatchPolicies.Create("Nearest", new LookScheduling(), ElevatorTiming.Default));
        Assert.IsType<CostBasedDispatch>(DispatchPolicies.Create("cost", new LookScheduling(), ElevatorTiming.Default));
        Assert.Throws<ArgumentException>(() => DispatchPolicies.Create("roundrobin", new LookScheduling(), ElevatorTiming.Default)); // removed (ADR-018)
    }
}
