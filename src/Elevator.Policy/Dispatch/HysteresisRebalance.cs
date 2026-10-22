using Elevator.Domain;
using Elevator.Domain.Control;

namespace Elevator.Policy.Dispatch;

/// <summary>
/// Optional rebalancing (ADR-006): periodically reconsider sticky assignments, and move one only when it clearly pays.
/// <list type="bullet">
/// <item><b>Hysteresis</b>: the best other elevator must beat the current one by more than H seconds of cost, so small
/// estimate noise never makes requests flap.</item>
/// <item><b>Commit point</b>: never move a request whose elevator is within stopping distance of the pickup: its
/// projected pickup is at most one floor of travel plus the door opening away. By then it is effectively there.</item>
/// <item><b>At most two moves per request</b>: under destination dispatch every move is visible to the passenger.</item>
/// </list>
/// Costs come from the same cost function as dispatch. The current elevator is evaluated with the request taken out
/// of its snapshot, so both sides compare "adding r" on equal terms.
/// </summary>
public sealed class HysteresisRebalance(ISchedulingPolicy scheduling, ElevatorTiming timing, double hysteresisS = 15, int maxMoves = 2) : IRebalancePolicy
{
    private readonly CostBasedDispatch _cost = new(scheduling, timing);
    private readonly long _commitWindowMs = timing.FloorTravelMs + timing.DoorOpeningMs;

    public int? BetterElevator(Request request, int currentElevator, int reassignmentsSoFar, IReadOnlyList<ElevatorSnapshot> snapshots, SimTime now)
    {
        if (reassignmentsSoFar >= maxMoves) return null;
        if (snapshots.FirstOrDefault(s => s.ElevatorId == currentElevator) is not { } current) return null;

        var without = Without(current, request.Id);
        var mine = _cost.Evaluate(without, request, now);
        if (mine.PickupAt is { } pickup && pickup.Ms - now.Ms <= _commitWindowMs) return null;

        int? best = null;
        var bestCost = mine.Cost - hysteresisS;
        foreach (var s in snapshots.Where(s => s.ElevatorId != currentElevator).OrderBy(s => s.ElevatorId))
        {
            var cost = _cost.Evaluate(s, request, now).Cost;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = s.ElevatorId;
            }
        }
        return best;
    }

    private static ElevatorSnapshot Without(ElevatorSnapshot s, int requestId) => s with
    {
        HallCalls = s.HallCalls
            .Select(c => (Call: c with { Requests = c.Requests.Where(r => r.Id != requestId).ToArray() }, Had: c.Requests.Any(r => r.Id == requestId)))
            .Where(x => !x.Had || x.Call.Requests.Count > 0)
            .Select(x => x.Call)
            .ToArray(),
    };
}
