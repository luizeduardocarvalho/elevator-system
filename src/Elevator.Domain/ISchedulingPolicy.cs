namespace Elevator.Domain;

/// <param name="Target">Floor to head for next (the elevator also always stops for drop-offs on the way); the
/// current floor means "open here"; null means nothing to do.</param>
/// <param name="ServeDirection">Lantern to show when it stops at <see cref="Target"/>. Null lets the mechanics
/// pick: the direction of the oldest pickup at that floor.</param>
public sealed record PolicyDecision(int? Target, Direction? ServeDirection = null)
{
    public static PolicyDecision None { get; } = new(Target: null);
}

/// <summary>
/// A per-elevator scheduling strategy (FIFO, LOOK...). Implementations live in Elevator.Policy and must be pure:
/// no state, no clock, no randomness. The mechanics around it are fixed (ADR-001, ADR-004).
/// </summary>
public interface ISchedulingPolicy
{
    string Name { get; }

    PolicyDecision Decide(ElevatorSnapshot snapshot);
}
