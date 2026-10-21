namespace Elevator.Domain;

/// <summary>What happens when an elevator is full (load at or above its bypass threshold). ADR-010.</summary>
public enum CapacityMode
{
    /// <summary>
    /// Default. Like a destination-dispatch panel, every request is given an elevator at once. A full elevator keeps
    /// its pickups but bypasses them (it only serves its riders) until riders get off and it has room, then it comes
    /// back: the passenger goes on its next trip. Cost-based dispatch sees that in the ETA.
    /// </summary>
    NextTrip,

    /// <summary>
    /// The v2 behaviour before ADR-010, kept for comparison. A full elevator hands back every pending pickup and
    /// rejects new ones; the dispatcher defers requests that no elevator can take.
    /// </summary>
    Defer,
}
