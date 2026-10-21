using System.Numerics;
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
///
/// Written with plain loops (no LINQ): it runs inside every route projection, so it is on the dispatch hot path.
/// </summary>
public sealed class LookScheduling : IFastSchedulingPolicy
{
    public string Name => "look";

    /// <summary>
    /// The same rules on floor bitmasks. "Lowest stop above" is a trailing-zero count, "highest below" a leading-zero
    /// count: O(1) per decision instead of a scan over every stop.
    /// </summary>
    public PolicyDecision Decide(in StopSet s)
    {
        if (s.IsEmpty) return PolicyDecision.None;

        Direction? servedHere = null;
        var direction = s.CommittedDirection ?? InitialDirection(s, out servedHere);
        if (direction is null) return new PolicyDecision(s.Floor, servedHere);

        return Sweep(s, direction.Value) ?? Sweep(s, Opposite(direction.Value)) ?? PolicyDecision.None;
    }

    private static PolicyDecision? Sweep(in StopSet s, Direction d)
    {
        var f = s.Floor;
        var here = StopSet.Bit(f);
        if (d == Direction.Up)
        {
            var same = (s.CarCalls & StopSet.Above(f)) | (s.UpPickups & (StopSet.Above(f) | here));
            if (same != 0) return new PolicyDecision(BitOperations.TrailingZeroCount(same), d);
            var turn = s.DownPickups & (StopSet.Above(f) | here);
            if (turn != 0) return new PolicyDecision(63 - BitOperations.LeadingZeroCount(turn), Direction.Down);
        }
        else
        {
            var same = (s.CarCalls & StopSet.Below(f)) | (s.DownPickups & (StopSet.Below(f) | here));
            if (same != 0) return new PolicyDecision(63 - BitOperations.LeadingZeroCount(same), d);
            var turn = s.UpPickups & (StopSet.Below(f) | here);
            if (turn != 0) return new PolicyDecision(BitOperations.TrailingZeroCount(turn), Direction.Up);
        }
        return null;
    }

    private static Direction? InitialDirection(in StopSet s, out Direction? servedHere)
    {
        servedHere = null;
        var f = s.Floor;
        Direction? hereDir = null; // oldest pickup at this floor
        foreach (var p in s.Pickups)
            if (p.Floor == f) { hereDir = p.Direction; break; }
        if (hereDir is not null && s.CarCalls == 0)
        {
            servedHere = hereDir;
            return null;
        }

        // Nearest stop elsewhere; ties: drop-offs first, then the oldest pickup, then the lower floor.
        int? nearest = null;
        var bestKey = (Distance: int.MaxValue, Order: long.MaxValue, Floor: int.MaxValue);
        var cars = s.CarCalls & ~StopSet.Bit(f);
        while (cars != 0)
        {
            var c = BitOperations.TrailingZeroCount(cars);
            cars &= cars - 1;
            var key = (Math.Abs(c - f), -1L, c);
            if (key.CompareTo(bestKey) < 0) { bestKey = key; nearest = c; }
        }
        foreach (var p in s.Pickups)
        {
            if (p.Floor == f) continue;
            var key = (Math.Abs(p.Floor - f), p.RegisteredAtMs, p.Floor);
            if (key.CompareTo(bestKey) < 0) { bestKey = key; nearest = p.Floor; }
        }

        if (nearest is null)
        {
            servedHere = hereDir;
            return null;
        }
        return Directions.Between(f, nearest.Value);
    }

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

        // 1. Same-direction stops ahead: drop-offs strictly ahead, pickups ahead or here. Nearest one wins.
        int? best = null;
        foreach (var c in s.CarCalls)
            if ((up ? c > f : c < f) && (best is null || (up ? c < best : c > best))) best = c;
        foreach (var h in s.HallCalls)
            if (h.Direction == d && (up ? h.Floor >= f : h.Floor <= f) && (best is null || (up ? h.Floor < best : h.Floor > best))) best = h.Floor;
        if (best is { } same) return new PolicyDecision(same, d);

        // 2. Opposite-direction pickups ahead: the farthest one is where this sweep turns.
        int? turn = null;
        foreach (var h in s.HallCalls)
            if (h.Direction != d && (up ? h.Floor >= f : h.Floor <= f) && (turn is null || (up ? h.Floor > turn : h.Floor < turn))) turn = h.Floor;
        if (turn is { } t) return new PolicyDecision(t, Opposite(d));

        return null;
    }

    private static Direction? InitialDirection(ElevatorSnapshot s, out Direction? servedHere)
    {
        servedHere = null;
        var f = s.CurrentFloor;
        HallCall? here = null; // oldest pickup at this floor
        foreach (var h in s.HallCalls)
            if (h.Floor == f) { here = h; break; }
        if (here is not null && s.CarCalls.Count == 0)
        {
            servedHere = here.Direction;
            return null;
        }

        // Nearest stop elsewhere; ties: drop-offs first (order −1), then the oldest pickup, then the lower floor.
        int? nearest = null;
        var bestKey = (Distance: int.MaxValue, Order: long.MaxValue, Floor: int.MaxValue);
        foreach (var c in s.CarCalls)
            Consider(c, -1L);
        foreach (var h in s.HallCalls)
            Consider(h.Floor, h.RegisteredAt.Ms);

        if (nearest is null)
        {
            servedHere = here?.Direction;
            return null;
        }
        return Directions.Between(f, nearest.Value);

        void Consider(int floor, long order)
        {
            if (floor == f) return;
            var key = (Math.Abs(floor - f), order, floor);
            if (key.CompareTo(bestKey) < 0)
            {
                bestKey = key;
                nearest = floor;
            }
        }
    }

    private static Direction Opposite(Direction d) => d == Direction.Up ? Direction.Down : Direction.Up;
}
