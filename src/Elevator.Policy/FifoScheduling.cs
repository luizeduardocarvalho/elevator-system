using System.Numerics;
using Elevator.Domain;

namespace Elevator.Policy;

/// <summary>
/// FIFO hall-call scheduling with real elevator mechanics (ADR-001; superseded as default by LOOK in ADR-004 but
/// kept selectable as the baseline).
///
/// Rules, in priority order:
/// <list type="number">
/// <item>Riders on board first. If drop-offs lie ahead in the committed direction, keep going and target the
/// farthest one (the elevator's mechanics stop at the nearer ones on the way). Otherwise turn towards the
/// nearest drop-off (ties: the lower floor, for determinism).</item>
/// <item>Otherwise, the oldest pending pickup, regardless of distance or direction. That is the FIFO part.</item>
/// <item>Otherwise no target: stay idle where it is.</item>
/// </list>
///
/// Pickups passed on the way are NOT served: the mechanics only stop at drop-offs and at the target.
/// No starvation: drop-offs come only from riders who boarded, and every pickup eventually becomes the oldest.
/// </summary>
public sealed class FifoScheduling : IFastSchedulingPolicy
{
    public string Name => "fifo";

    public PolicyDecision Decide(ElevatorSnapshot s) => new(NextGoal(s));

    public PolicyDecision Decide(in StopSet s)
    {
        if (s.CarCalls != 0)
        {
            if (s.CommittedDirection is { } direction)
            {
                var ahead = s.CarCalls & (direction == Direction.Up ? StopSet.Above(s.Floor) : StopSet.Below(s.Floor));
                if (ahead != 0)
                    return new PolicyDecision(direction == Direction.Up ? 63 - BitOperations.LeadingZeroCount(ahead) : BitOperations.TrailingZeroCount(ahead));
            }

            var below = s.CarCalls & StopSet.Below(s.Floor);
            var atOrAbove = s.CarCalls & ~StopSet.Below(s.Floor);
            int? low = below != 0 ? 63 - BitOperations.LeadingZeroCount(below) : null;
            int? high = atOrAbove != 0 ? BitOperations.TrailingZeroCount(atOrAbove) : null;
            if (low is null) return new PolicyDecision(high);
            if (high is null) return new PolicyDecision(low);
            return new PolicyDecision(s.Floor - low.Value <= high.Value - s.Floor ? low : high);
        }

        return s.Pickups.IsEmpty ? PolicyDecision.None : new PolicyDecision(s.Pickups[0].Floor);
    }

    public static int? NextGoal(ElevatorSnapshot s)
    {
        if (s.CarCalls.Count > 0)
        {
            if (s.CommittedDirection is { } direction)
            {
                var ahead = s.CarCalls.Where(c => direction.IsAhead(s.CurrentFloor, c)).ToList();
                if (ahead.Count > 0) return direction == Direction.Up ? ahead.Max() : ahead.Min();
            }

            return s.CarCalls.OrderBy(c => Math.Abs(c - s.CurrentFloor)).ThenBy(c => c).First();
        }

        return s.HallCalls.Count > 0 ? s.HallCalls[0].Floor : null;
    }
}
