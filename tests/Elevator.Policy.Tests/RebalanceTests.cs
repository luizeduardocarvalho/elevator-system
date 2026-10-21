using Elevator.Domain;
using Elevator.Policy.Dispatch;

namespace Elevator.Policy.Tests;

/// <summary>Hysteresis rebalancing (ADR-006). Timing: 2 s/floor, doors 1 + 3 + 1 s.</summary>
public class RebalanceTests
{
    private static readonly LookScheduling Look = new();
    private static readonly HysteresisRebalance Rebalance = new(Look, ElevatorTiming.Default);

    private static Request Req(int id, int from, int to) => Request.Create(from, to, SimTime.Zero, 20, id: id).Value!;

    private static ElevatorSnapshot Car(int id, int floor, Direction? dir = null, int[]? carCalls = null, HallCall[]? pickups = null,
        ElevatorState state = ElevatorState.Idle, SimTime? phaseEnds = null) =>
        ElevatorSnapshot.ForPolicy(floor, state, dir, carCalls ?? [], pickups ?? [], 20, id) with
        {
            Capacity = 10,
            BypassThreshold = 0.8,
            PhaseEndsAt = phaseEnds,
        };

    private static HallCall Pickup(Request r) => new(r.PickupFloor, r.Direction, SimTime.Zero, [r]);

    [Fact]
    public void Moves_a_request_when_another_elevator_is_better_by_more_than_the_hysteresis()
    {
        // R (10→15) is assigned to E0 idle at 1 (ETA 18 + 1 = 19 s). E1 is idle at 10: ETA 1 s (doors only).
        // 19 − 1 = 18 s > H = 15 s → move to E1.
        var r = Req(1, 10, 15);
        var e0 = Car(0, 1, pickups: [Pickup(r)]);
        Assert.Equal(1, Rebalance.BetterElevator(r, 0, 0, [e0, Car(1, 10)], SimTime.Zero));
    }

    [Fact]
    public void Does_not_move_within_the_hysteresis()
    {
        // E1 idle at 4: ETA 12 + 1 = 13 s against 19 s → only 6 s better → stay.
        var r = Req(1, 10, 15);
        Assert.Null(Rebalance.BetterElevator(r, 0, 0, [Car(0, 1, pickups: [Pickup(r)]), Car(1, 4)], SimTime.Zero));
    }

    [Fact]
    public void Does_not_move_past_the_commit_point()
    {
        // E0 is one floor away and heading for the pickup (it will be level with floor 10 at t = 2 s): committed.
        var r = Req(1, 10, 15);
        var e0 = Car(0, 9, Direction.Up, pickups: [Pickup(r)], state: ElevatorState.MovingUp, phaseEnds: new SimTime(2_000));
        Assert.Null(Rebalance.BetterElevator(r, 0, 0, [e0, Car(1, 10)], SimTime.Zero));
    }

    [Fact]
    public void A_request_moves_at_most_twice()
    {
        var r = Req(1, 10, 15);
        var e0 = Car(0, 1, pickups: [Pickup(r)]);
        Assert.Null(Rebalance.BetterElevator(r, 0, 2, [e0, Car(1, 10)], SimTime.Zero));
    }
}
