using Elevator.Domain;
using Elevator.Policy.Dispatch;

namespace Elevator.Policy.Tests;

public class DispatchTests
{
    private static Request Req(int from, int to, int id = 1) => Request.Create(from, to, SimTime.Zero, 20, id: id).Value!;

    private static ElevatorSnapshot Car(int id, int floor, Direction? dir = null, int[]? carCalls = null, ElevatorState state = ElevatorState.Idle, int load = 0,
        CapacityMode mode = CapacityMode.NextTrip) =>
        ElevatorSnapshot.ForPolicy(floor, state, dir, carCalls ?? [], [], 20, id) with
        {
            Capacity = 10,
            BypassThreshold = 0.8,
            Riders = Enumerable.Range(100, load).Select(i => Req(1, 2, i)).ToArray(),
            CapacityMode = mode,
        };

    private static DispatchContext Ctx(Request r, long seq = 0, params ElevatorSnapshot[] cars) =>
        new(r, cars, SimTime.Zero, ElevatorTiming.Default, seq);

    [Fact]
    public void Nearest_idle_elevator_wins() =>
        Assert.Equal(1, new NearestDispatch().Choose(Ctx(Req(8, 1), 0, Car(0, 1), Car(1, 10)))!.ElevatorId);

    [Fact]
    public void Same_direction_ahead_counts_plain_distance()
    {
        var e0 = Car(0, 3, Direction.Up, [12], ElevatorState.MovingUp);
        Assert.Equal(5, NearestDispatch.EffectiveDistance(e0, Req(8, 15)));
    }

    [Fact]
    public void Opposite_direction_must_finish_the_sweep_first()
    {
        var e0 = Car(0, 3, Direction.Up, [12], ElevatorState.MovingUp);
        Assert.Equal(13, NearestDispatch.EffectiveDistance(e0, Req(8, 1)));
        Assert.Equal(1, new NearestDispatch().Choose(Ctx(Req(8, 1), 0, e0, Car(1, 14)))!.ElevatorId);
    }

    [Fact]
    public void Behind_a_moving_elevator_counts_the_sweep_and_back()
    {
        Assert.Equal(14, NearestDispatch.EffectiveDistance(Car(0, 10, Direction.Up, [15], ElevatorState.MovingUp), Req(6, 9)));
    }

    [Fact]
    public void Ties_go_to_the_lowest_id() =>
        Assert.Equal(0, new NearestDispatch().Choose(Ctx(Req(5, 9), 0, Car(1, 3), Car(0, 7)))!.ElevatorId);

    [Fact]
    public void Elevators_with_room_are_preferred_and_maintenance_is_never_chosen()
    {
        var full = Car(0, 5, load: 8);
        var maintenance = Car(1, 5, state: ElevatorState.Maintenance);
        var far = Car(2, 20);
        Assert.Equal(2, new NearestDispatch().Choose(Ctx(Req(5, 9), 0, full, maintenance, far))!.ElevatorId);
    }

    [Fact]
    public void Next_trip_when_every_elevator_is_full_one_is_still_named()
    {
        var full = Car(0, 5, load: 8);
        var maintenance = Car(1, 5, state: ElevatorState.Maintenance);
        Assert.Equal(0, new NearestDispatch().Choose(Ctx(Req(5, 9), 0, full, maintenance))!.ElevatorId);
        Assert.Equal(0, new RoundRobinDispatch().Choose(Ctx(Req(5, 9), 0, full, maintenance))!.ElevatorId);
    }

    [Fact]
    public void Defer_when_every_elevator_is_full_the_request_waits()
    {
        var full = Car(0, 5, load: 8, mode: CapacityMode.Defer);
        var maintenance = Car(1, 5, state: ElevatorState.Maintenance, mode: CapacityMode.Defer);
        Assert.Null(new NearestDispatch().Choose(Ctx(Req(5, 9), 0, full, maintenance)));
        Assert.Null(new RoundRobinDispatch().Choose(Ctx(Req(5, 9), 0, full, maintenance)));
    }

    [Fact]
    public void Round_robin_rotates_over_eligible_elevators_by_sequence()
    {
        ElevatorSnapshot[] cars = [Car(0, 1), Car(1, 1, load: 9), Car(2, 1), Car(3, 1)];
        Assert.Equal([0, 2, 3, 0, 2], Enumerable.Range(0, 5).Select(seq => new RoundRobinDispatch().Choose(Ctx(Req(5, 9), seq, cars))!.ElevatorId));
    }

    [Fact]
    public void Dispatch_policies_are_registered_by_name()
    {
        Assert.IsType<NearestDispatch>(DispatchPolicies.Create("nearest", new FifoScheduling(), ElevatorTiming.Default));
        Assert.IsType<RoundRobinDispatch>(DispatchPolicies.Create("RoundRobin", new FifoScheduling(), ElevatorTiming.Default));
        Assert.Throws<ArgumentException>(() => DispatchPolicies.Create("psychic", new FifoScheduling(), ElevatorTiming.Default));
    }
}
