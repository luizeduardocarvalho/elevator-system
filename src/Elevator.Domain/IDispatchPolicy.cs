namespace Elevator.Domain;

/// <summary>Everything a dispatch policy may look at. <see cref="Candidates"/> already excludes elevators that rejected this request.</summary>
/// <param name="Sequence">Ordinal of this dispatch decision (deterministic); round-robin uses it.</param>
public sealed record DispatchContext(Request Request, IReadOnlyList<ElevatorSnapshot> Candidates, SimTime Now, ElevatorTiming Timing, long Sequence);

/// <param name="Cost">Score of the chosen elevator, lower is better; units depend on the policy.</param>
/// <param name="Eta">Estimated pickup time, when the policy computes one (used for the staleness check).</param>
public sealed record DispatchChoice(int ElevatorId, double Cost, SimTime? Eta = null);

/// <summary>
/// A dispatch strategy: which elevator should serve this request? Pure function over snapshots
/// (Elevator.Policy implements RoundRobin, Nearest, CostBased). Null means no candidate is eligible.
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
    SimTime? EstimatePickup(ElevatorSnapshot snapshot, Request request, SimTime now);
}
