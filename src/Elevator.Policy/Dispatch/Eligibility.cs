using Elevator.Domain;

namespace Elevator.Policy.Dispatch;

public static class Eligibility
{
    /// <summary>Can take a new pickup right now: not in maintenance and below the full-load bypass threshold.</summary>
    public static bool CanTakePickup(ElevatorSnapshot s) => s.State != ElevatorState.Maintenance && !s.IsFull;

    /// <summary>
    /// Elevators a simple dispatcher may choose from: those with room. In NextTrip mode (ADR-010) a request is never
    /// left without an elevator, so when every one is full they are all candidates (for their next trip); in Defer
    /// mode the request waits instead. Elevators in maintenance are never candidates.
    /// </summary>
    public static IReadOnlyList<ElevatorSnapshot> Candidates(IReadOnlyList<ElevatorSnapshot> all)
    {
        var withRoom = all.Where(CanTakePickup).ToList();
        if (withRoom.Count > 0) return withRoom;
        return all.Where(s => s.State != ElevatorState.Maintenance && s.CapacityMode == CapacityMode.NextTrip).ToList();
    }
}
