using Elevator.Domain;

namespace Elevator.Policy.Access;

/// <summary>Access configuration of a building (ADR-012); YAML <c>access:</c>.</summary>
/// <param name="RestrictedFloors">Floor → minimum access level to be picked up there or taken there.</param>
/// <param name="VipElevators">Elevators dedicated to VIPs (everyone else is denied on them).</param>
/// <param name="VipPriority">A VIP request does not pay for the delay it adds to others in the cost function, so it takes
/// the fastest pickup (ADR-015). It never reorders requests.</param>
public sealed record AccessConfig(IReadOnlyDictionary<int, int> RestrictedFloors, IReadOnlyList<int> VipElevators, bool VipPriority)
{
    public static AccessConfig None { get; } = new(new Dictionary<int, int>(), [], false);

    /// <summary>
    /// The access pipeline (ADR-012), in this order: served floors, capacity units, floor restrictions, VIP. The first
    /// two are always on and inert in the default config (every floor served, every load fits); the last two only
    /// when configured. Order decides which reason is logged, never whether access is granted.
    /// </summary>
    public AccessPipeline CreatePipeline()
    {
        List<IAccessPolicy> policies = [new ServedFloorsPolicy(), new CapacityUnitsPolicy()];
        if (RestrictedFloors.Count > 0) policies.Add(new FloorRestrictionPolicy(RestrictedFloors));
        if (VipElevators.Count > 0) policies.Add(new VipPolicy(VipElevators));
        return new AccessPipeline(policies);
    }
}
