namespace Elevator.Domain;

/// <summary>
/// A scheduling strategy: given a snapshot, which floor should the elevator head for next?
/// Implementations live in Elevator.Policy and must be pure (no state, no clock, no randomness).
///
/// The elevator's mechanics are independent of the policy: it always stops for car calls on the way, and it
/// stops at the goal returned here. Returning the current floor means "open the doors here".
/// </summary>
public interface ISchedulingPolicy
{
    string Name { get; }

    int? NextGoal(ElevatorSnapshot snapshot);
}
