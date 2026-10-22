namespace Elevator.Domain;

/// <summary>Everything a dispatch policy may look at. <see cref="Candidates"/> already excludes elevators that rejected this request.</summary>
public sealed record DispatchContext(Request Request, IReadOnlyList<ElevatorSnapshot> Candidates, Instant Now, ElevatorTiming Timing)
{
    /// <summary>Also report every candidate's cost in <see cref="DispatchChoice.Candidates"/> (shared v3 contract).</summary>
    public bool Explain { get; init; }

    /// <summary>VIP priority (ADR-015): a cost policy may stop charging a VIP request for the delay it adds to others.</summary>
    public bool VipPriority { get; init; }
}

/// <param name="Cost">Score of the chosen elevator, lower is better; units depend on the policy.</param>
/// <param name="Eta">Estimated pickup time, when the policy computes one (used for the staleness check).</param>
public sealed record DispatchChoice(int ElevatorId, double Cost, Instant? Eta = null)
{
    /// <summary>Every candidate's cost, when <see cref="DispatchContext.Explain"/> was set; null otherwise.</summary>
    public IReadOnlyList<Events.CandidateCost>? Candidates { get; init; }
}

/// <summary>
/// A dispatch strategy: which elevator should serve this request? Pure function over snapshots
/// (Elevator.Policy implements Nearest and CostBased). Null means no candidate is eligible.
/// </summary>
public interface IDispatchPolicy
{
    string Name { get; }

    DispatchChoice? Choose(DispatchContext context);
}

/// <summary>
/// Estimates when <paramref name="request"/> would be picked up if assigned at <paramref name="now"/> to the elevator
/// described by <paramref name="snapshot"/> (which may be older than now).
/// </summary>
public interface IEtaEstimator
{
    Instant? EstimatePickup(ElevatorSnapshot snapshot, Request request, Instant now);
}
