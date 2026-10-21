using Elevator.Domain;

namespace Elevator.Policy.Dispatch;

/// <summary>
/// "Closest elevator, same-direction priority" (spec). The baseline dispatcher.
///
/// Effective distance from elevator to the pickup, in floors:
/// <list type="bullet">
/// <item>Idle (no committed direction): |floor − pickup|.</item>
/// <item>Committed to direction d, the pickup lies ahead in d and the request also goes d: |floor − pickup|
/// (it can serve it on the way).</item>
/// <item>Otherwise it must finish its sweep first: distance to the farthest stop ahead in d, then back to the
/// pickup: |far − floor| + |far − pickup|.</item>
/// </list>
/// Ties go to the lowest elevator id. Elevators in maintenance are never chosen; full ones only when every elevator is
/// full and the building runs in NextTrip mode (ADR-010).
/// </summary>
public sealed class NearestDispatch : IDispatchPolicy
{
    public string Name => "nearest";

    public DispatchChoice? Choose(DispatchContext context)
    {
        DispatchChoice? best = null;
        foreach (var s in Eligibility.Candidates(context.Candidates).OrderBy(s => s.ElevatorId))
        {
            var distance = EffectiveDistance(s, context.Request);
            if (best is null || distance < best.Cost) best = new DispatchChoice(s.ElevatorId, distance);
        }
        return best;
    }

    public static int EffectiveDistance(ElevatorSnapshot s, Request r)
    {
        var floor = s.CurrentFloor;
        if (s.CommittedDirection is not { } d) return Math.Abs(floor - r.PickupFloor);

        if (r.Direction == d && (r.PickupFloor == floor ? !s.IsMoving : d.IsAhead(floor, r.PickupFloor)))
            return Math.Abs(floor - r.PickupFloor);

        var stopsAhead = s.CarCalls.Concat(s.HallCalls.Select(c => c.Floor)).Where(f => d.IsAhead(floor, f)).ToList();
        var far = stopsAhead.Count == 0 ? floor : d == Direction.Up ? stopsAhead.Max() : stopsAhead.Min();
        return Math.Abs(far - floor) + Math.Abs(far - r.PickupFloor);
    }
}
