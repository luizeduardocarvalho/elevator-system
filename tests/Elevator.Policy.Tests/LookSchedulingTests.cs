using Elevator.Domain;

namespace Elevator.Policy.Tests;

public class LookSchedulingTests
{
    private static readonly LookScheduling Look = new();

    private static ElevatorSnapshot At(int floor, Direction? committed, int[] carCalls, params (int Floor, Direction Direction)[] pickups) =>
        ElevatorSnapshot.ForPolicy(floor, ElevatorState.Idle, committed, carCalls,
            pickups.Select((c, i) => new HallCall(c.Floor, c.Direction, new SimTime(i * 1000))).ToArray(), 20);

    private static void Expect(ElevatorSnapshot s, int? target, Direction? serve = null)
    {
        var d = Look.Decide(s);
        Assert.Equal(target, d.Target);
        if (serve is not null) Assert.Equal(serve, d.ServeDirection);
    }

    [Fact]
    public void Nothing_to_do() => Expect(At(5, Direction.Up, []), null);

    [Fact]
    public void Idle_heads_for_the_nearest_stop() => Expect(At(5, null, [], (12, Direction.Down), (3, Direction.Up)), 3, Direction.Up);

    [Fact]
    public void Moving_up_takes_the_lowest_up_stop_ahead_pickups_included() =>
        Expect(At(5, Direction.Up, [7, 9], (6, Direction.Up), (8, Direction.Down)), 6, Direction.Up);

    [Fact]
    public void Down_call_above_while_moving_up_with_nothing_beyond_goes_there_and_reverses() =>
        Expect(At(5, Direction.Up, [], (8, Direction.Down)), 8, Direction.Down);

    [Fact]
    public void Down_call_above_while_moving_up_waits_for_up_stops_beyond_it()
    {
        Expect(At(5, Direction.Up, [10], (8, Direction.Down)), 10, Direction.Up); // passes 8 on the way up
        Expect(At(10, Direction.Up, [], (8, Direction.Down)), 8, Direction.Down);  // served on the way back down
    }

    [Fact]
    public void Highest_down_call_above_is_the_turning_point() =>
        Expect(At(5, Direction.Up, [], (8, Direction.Down), (14, Direction.Down)), 14, Direction.Down);

    [Fact]
    public void Up_call_below_while_moving_up_is_served_after_the_down_sweep()
    {
        Expect(At(5, Direction.Up, [9], (2, Direction.Up)), 9, Direction.Up);
        Expect(At(9, Direction.Up, [], (2, Direction.Up)), 2, Direction.Up); // turn, go down to the lowest up call
        Expect(At(9, Direction.Up, [4], (2, Direction.Up), (6, Direction.Down)), 6, Direction.Down); // down stops first
    }

    [Fact]
    public void Pickup_at_the_current_floor()
    {
        Expect(At(5, Direction.Up, [9], (5, Direction.Up)), 5, Direction.Up);     // same direction: serve now
        Expect(At(5, Direction.Up, [9], (5, Direction.Down)), 9, Direction.Up);   // opposite: later
        Expect(At(5, Direction.Up, [], (5, Direction.Down)), 5, Direction.Down);  // opposite, nothing ahead: reverse here
    }

    [Fact]
    public void Reverses_at_the_top_floor() => Expect(At(20, Direction.Up, [3], (11, Direction.Down)), 11, Direction.Down);

    [Fact]
    public void Moving_down_is_symmetric()
    {
        Expect(At(15, Direction.Down, [3, 10], (12, Direction.Down), (13, Direction.Up)), 12, Direction.Down);
        Expect(At(15, Direction.Down, [], (13, Direction.Up), (4, Direction.Up)), 4, Direction.Up);
    }

    [Fact]
    public void Is_registered_under_its_name() => Assert.IsType<LookScheduling>(SchedulingPolicies.Create("look"));
}
