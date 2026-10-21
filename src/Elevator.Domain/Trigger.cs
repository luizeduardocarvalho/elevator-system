namespace Elevator.Domain;

/// <summary>Inputs of the elevator state machine. Every state change goes through one of these (ADR-002).</summary>
public enum Trigger
{
    StartMoveUp,
    StartMoveDown,
    /// <summary>Stop at the current floor and start opening the doors (from a moving state on arrival, or from Idle).</summary>
    ArriveAndOpen,
    DoorsFullyOpen,
    StartClosing,
    DoorsFullyClosed,
    /// <summary>A matching hall call while the doors are closing reverses them.</summary>
    Reopen,
    EnterMaintenance,
    ExitMaintenance,
    /// <summary>Emergency stop while moving: stop level with the next floor, doors staying closed (ADR-013).</summary>
    EmergencyHalt,
    /// <summary>Emergency stop during a door cycle: the doors close (reversing if still opening) and stay closed (ADR-013).</summary>
    EmergencyClose,
}
