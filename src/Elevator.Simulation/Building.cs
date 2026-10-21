using Elevator.Domain;

namespace Elevator.Simulation;

/// <summary>
/// Builds the parts of a building that both shells share (the sim engine and the actor runtime), so that the same
/// configuration gives the same elevators in both (outcome equivalence, ADR-005).
/// </summary>
public static class Building
{
    /// <summary>An elevator of <paramref name="type"/> (ADR-012), starting idle at the lowest floor it serves.</summary>
    public static ElevatorUnit CreateElevator(int id, int floors, ElevatorType type, ISchedulingPolicy scheduling, double bypassThreshold,
        CapacityMode capacityMode) =>
        new(id, floors, type.Timing, scheduling, type.LowestServed(floors), type.Capacity, bypassThreshold, capacityMode, type.ServedFloors, type.Name);
}
