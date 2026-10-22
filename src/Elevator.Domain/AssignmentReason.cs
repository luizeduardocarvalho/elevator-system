namespace Elevator.Domain;

public enum AssignmentReason
{
    /// <summary>The elevator's own ETA is much worse than the dispatcher's estimate (stale snapshot).</summary>
    Stale,
    /// <summary>Full (at or above the bypass threshold).</summary>
    Capacity,
    Maintenance,
    /// <summary>The elevator came but was full at the door; the passenger presses again.</summary>
    LeftBehind,
    /// <summary>The watchdog declared the elevator faulty; its pickups are handed back (shared v3 contract).</summary>
    Fault,
    /// <summary>Emergency stop; its pickups are handed back (shared v3 contract).</summary>
    Emergency,
}
