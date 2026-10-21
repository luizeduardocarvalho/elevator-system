using Elevator.Domain;

namespace Elevator.Policy.Dispatch;

public static class Eligibility
{
    /// <summary>Can take a new pickup right now: not in maintenance and below the full-load bypass threshold.</summary>
    public static bool CanTakePickup(ElevatorSnapshot s) => s.State != ElevatorState.Maintenance && !s.IsFull;
}
