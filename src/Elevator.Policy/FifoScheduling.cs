using Elevator.Domain;

namespace Elevator.Policy;

/// <summary>
/// FIFO hall-call scheduling with real elevator mechanics (ADR-001).
///
/// Rules, in priority order:
/// <list type="number">
/// <item>Riders on board first. If car calls lie ahead in the committed direction, keep going and target the
/// farthest one (the elevator's mechanics stop at the nearer ones on the way). Otherwise turn towards the
/// nearest car call (ties: the lower floor, for determinism).</item>
/// <item>Otherwise, the oldest pending hall call, regardless of distance or direction. That is the FIFO part.</item>
/// <item>Otherwise no goal: stay idle where it is.</item>
/// </list>
///
/// Hall calls passed on the way are NOT picked up: the mechanics only stop at car calls and at the goal.
/// This keeps FIFO an honest baseline; collective pick-up is the measured improvement in v2.
/// No starvation: car calls come only from riders who boarded, and every hall call eventually becomes the oldest.
/// </summary>
public sealed class FifoScheduling : ISchedulingPolicy
{
    public string Name => "fifo";

    public int? NextGoal(ElevatorSnapshot s)
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
