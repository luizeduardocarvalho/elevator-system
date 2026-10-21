namespace Elevator.Domain;

/// <summary>Why an assignment was rejected or released. Drives the wait-time attribution buckets (ADR-008).</summary>
public enum AssignmentReason
{
    /// <summary>The elevator's own ETA is much worse than the dispatcher's estimate (stale snapshot).</summary>
    Stale,
    /// <summary>Full (at or above the bypass threshold).</summary>
    Capacity,
    Maintenance,
    /// <summary>Became full after accepting: full-load bypass hands pending pickups back.</summary>
    BypassedFull,
    /// <summary>Moved to a better elevator by the optional rebalancer.</summary>
    Rebalance,
    /// <summary>The elevator came but was full at the door; the passenger presses again.</summary>
    LeftBehind,
}
