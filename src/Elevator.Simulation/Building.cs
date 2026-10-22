using Elevator.Domain;
using Elevator.Policy.Access;

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

    /// <summary>
    /// The access pipeline (ADR-012), in this order: served floors, capacity units, floor restrictions, VIP. The first
    /// two are always on and inert in the default config (every floor served, every load fits); the last two only
    /// when configured. Order decides which reason is logged, never whether access is granted.
    /// </summary>
    public static AccessPipeline CreateAccess(AccessConfig access)
    {
        List<IAccessPolicy> policies = [new ServedFloorsPolicy(), new CapacityUnitsPolicy()];
        if (access.RestrictedFloors.Count > 0) policies.Add(new FloorRestrictionPolicy(access.RestrictedFloors));
        if (access.VipElevators.Count > 0) policies.Add(new VipPolicy(access.VipElevators));
        return new AccessPipeline(policies);
    }
}
