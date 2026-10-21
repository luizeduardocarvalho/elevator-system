using Elevator.Domain;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Policy.Tests;

public class FifoSchedulingTests
{
    private static readonly FifoScheduling Fifo = new();

    private static ElevatorSnapshot At(int floor, Direction? committed = null, int[]? carCalls = null, params (int Floor, Direction Direction)[] hallCalls) =>
        ElevatorSnapshot.ForPolicy(floor, ElevatorState.Idle, committed, carCalls ?? [],
            hallCalls.Select((c, i) => new HallCall(c.Floor, c.Direction, new SimTime(i * 1000))).ToArray(), 10);

    [Fact]
    public void No_calls_means_no_goal() => Assert.Null(Fifo.Decide(At(5)).Target);

    [Fact]
    public void Oldest_hall_call_wins_whatever_its_distance() =>
        Assert.Equal(9, Fifo.Decide(At(5, hallCalls: [(9, Direction.Down), (6, Direction.Up), (5, Direction.Down)])).Target);

    [Fact]
    public void Riders_on_board_come_before_any_hall_call() =>
        Assert.Equal(2, Fifo.Decide(At(5, Direction.Down, [2], (6, Direction.Up))).Target);

    [Fact]
    public void Keeps_going_while_car_calls_lie_ahead_and_targets_the_farthest() =>
        Assert.Equal(9, Fifo.Decide(At(5, Direction.Up, [2, 7, 9])).Target);

    [Fact]
    public void Turns_towards_the_nearest_car_call_when_none_lie_ahead() =>
        Assert.Equal(3, Fifo.Decide(At(5, Direction.Up, [1, 3])).Target);

    [Fact]
    public void Without_a_direction_picks_the_nearest_car_call_and_breaks_ties_downwards() =>
        Assert.Equal(3, Fifo.Decide(At(5, null, [3, 7])).Target);

    [Fact]
    public void Is_registered_under_its_name() =>
        Assert.IsType<FifoScheduling>(SchedulingPolicies.Create("fifo"));

    [Fact]
    public void Unknown_policy_name_is_rejected() =>
        Assert.Throws<ArgumentException>(() => SchedulingPolicies.Create("nope"));

    [Property(MaxTest = 500)]
    public Property Goal_is_always_a_pending_call_floor_and_null_only_without_calls()
    {
        var gen =
            from floor in Gen.Choose(1, 10)
            from dir in Gen.Elements<Direction?>(null, Direction.Up, Direction.Down)
            from cars in Gen.ArrayOf(Gen.Choose(1, 10))
            from halls in Gen.ArrayOf(Gen.Choose(1, 10).Zip(Gen.Elements(Direction.Up, Direction.Down)))
            select At(floor, dir, [.. cars.Distinct().Order()], halls);

        return Prop.ForAll(gen.ToArbitrary(), s =>
        {
            var goal = Fifo.Decide(s).Target;
            var floors = s.CarCalls.Concat(s.HallCalls.Select(c => c.Floor)).ToHashSet();
            Assert.Equal(floors.Count == 0, goal is null);
            if (goal is { } g) Assert.Contains(g, floors);
            Assert.Equal(goal, Fifo.Decide(s).Target); // pure: same input, same answer
        });
    }
}
