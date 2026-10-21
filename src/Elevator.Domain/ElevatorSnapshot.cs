namespace Elevator.Domain;

/// <summary>
/// Immutable view of one elevator handed to scheduling policies, so a policy is a pure function of it.
/// </summary>
/// <param name="CommittedDirection">Direction of travel while moving; the lit lantern while stopped; null when it has no work.</param>
/// <param name="CarCalls">Pending car calls, ascending.</param>
/// <param name="HallCalls">Pending hall calls in registration order (oldest first).</param>
public sealed record ElevatorSnapshot(
    int ElevatorId,
    int CurrentFloor,
    ElevatorState State,
    Direction? CommittedDirection,
    IReadOnlyList<int> CarCalls,
    IReadOnlyList<HallCall> HallCalls,
    int Floors);
