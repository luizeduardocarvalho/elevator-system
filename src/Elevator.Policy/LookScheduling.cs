using Elevator.Domain;

namespace Elevator.Policy;

/// <summary>
/// LOOK (ADR-004): sweep in one direction serving every stop on the way, and reverse only when nothing lies ahead
/// (unlike SCAN, it does not run to the end of the shaft).
///
/// Stops are split into two sorted sets:
/// <list type="bullet">
/// <item><b>up stops</b>: up pickups, and drop-offs above the elevator;</item>
/// <item><b>down stops</b>: down pickups, and drop-offs below the elevator.</item>
/// </list>
/// Going up from floor f:
/// <list type="number">
/// <item>the lowest up stop at or above f (a pickup at f counts: serve it now);</item>
/// <item>else the highest down pickup at or above f: go there and turn (it is the top of this sweep);</item>
/// <item>else reverse: the highest down stop at or below f, then the lowest up pickup below (the bottom of the
/// next sweep).</item>
/// </list>
/// Going down is symmetric. When idle, it heads for the nearest stop (ties: oldest pickup, then lower floor).
/// The returned serve direction is the lantern at the target, so the right riders board.
/// </summary>
public sealed class LookScheduling : ISchedulingPolicy
{
    public string Name => "look";

    public PolicyDecision Decide(ElevatorSnapshot s)
    {
        if (s.CarCalls.Count == 0 && s.HallCalls.Count == 0) return PolicyDecision.None;

        Direction? servedHere = null;
        var direction = s.CommittedDirection ?? InitialDirection(s, out servedHere);
        if (direction is null) return new PolicyDecision(s.CurrentFloor, servedHere); // a pickup right here

        return Sweep(s, direction.Value) ?? Sweep(s, Opposite(direction.Value)) ?? PolicyDecision.None;
    }

    /// <summary>Target and lantern when sweeping in <paramref name="d"/>, or null when nothing at all is served by sweeping that way.</summary>
    private static PolicyDecision? Sweep(ElevatorSnapshot s, Direction d)
    {
        var f = s.CurrentFloor;
        var up = d == Direction.Up;
        bool Ahead(int floor) => up ? floor >= f : floor <= f;

        // 1. Same-direction stops ahead: drop-offs strictly ahead, pickups ahead or here.
        var sameDirection = s.CarCalls.Where(c => up ? c > f : c < f)
            .Concat(s.HallCalls.Where(c => c.Direction == d && Ahead(c.Floor)).Select(c => c.Floor))
            .ToList();
        if (sameDirection.Count > 0) return new PolicyDecision(up ? sameDirection.Min() : sameDirection.Max(), d);

        // 2. Opposite-direction pickups ahead: the farthest one is where this sweep turns.
        var turning = s.HallCalls.Where(c => c.Direction != d && Ahead(c.Floor)).Select(c => c.Floor).ToList();
        if (turning.Count > 0) return new PolicyDecision(up ? turning.Max() : turning.Min(), Opposite(d));

        return null;
    }

    private static Direction? InitialDirection(ElevatorSnapshot s, out Direction? servedHere)
    {
        servedHere = null;
        var f = s.CurrentFloor;
        var here = s.HallCalls.FirstOrDefault(c => c.Floor == f); // oldest pickup at this floor
        if (here is not null && s.CarCalls.Count == 0)
        {
            servedHere = here.Direction;
            return null;
        }

        var nearest = s.CarCalls.Select(c => (Floor: c, Order: -1L))
            .Concat(s.HallCalls.Select(c => (c.Floor, Order: c.RegisteredAt.Ms)))
            .Where(x => x.Floor != f)
            .OrderBy(x => Math.Abs(x.Floor - f)).ThenBy(x => x.Order).ThenBy(x => x.Floor)
            .Select(x => (int?)x.Floor)
            .FirstOrDefault();
        if (nearest is null)
        {
            servedHere = here?.Direction;
            return null;
        }
        return Directions.Between(f, nearest.Value);
    }

    private static Direction Opposite(Direction d) => d == Direction.Up ? Direction.Down : Direction.Up;
}
