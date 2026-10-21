namespace Elevator.Domain;

/// <summary>
/// Elevator states exactly as listed in the spec (same members, same order). C# casing here;
/// the event log serializes them in the spec spelling (e.g. MOVING_UP). See ADR-002.
/// </summary>
public enum ElevatorState
{
    Idle,
    MovingUp,
    MovingDown,
    DoorOpening,
    DoorOpen,
    DoorClosing,
    Maintenance,
}
