namespace Elevator.Domain;

/// <summary>
/// Operating mode of an elevator, alongside the spec's <see cref="ElevatorState"/> (which is frozen and has no
/// Emergency or Fault). Part of the shared v3 contract (docs/contract-v3.md).
/// </summary>
public enum ElevatorMode
{
    Normal,
    /// <summary>Maintenance requested: takes no new assignments, delivers its riders, then parks.</summary>
    Draining,
    /// <summary>Parked out of service (its state is <see cref="ElevatorState.Maintenance"/>).</summary>
    Maintenance,
    /// <summary>Emergency stop in effect until cleared.</summary>
    Emergency,
    /// <summary>The watchdog saw no progress for too long; its calls were reassigned.</summary>
    Fault,
}
