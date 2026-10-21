using Elevator.Domain;

namespace Elevator.Policy.Dispatch;

/// <param name="Lambda">Weight of the delay the new pickup adds to people already assigned or riding (seconds per second).</param>
/// <param name="Mu">Seconds of penalty for an elevator that would be completely full at pickup (scaled by L̂/C).</param>
public sealed record CostWeights(double Lambda = 0.5, double Mu = 10.0) // tuned by bench sweep, ADR-006
{
    public static CostWeights Default { get; } = new();
}

/// <summary>Breakdown of one elevator's cost for one request, in seconds.</summary>
public sealed record CostEvaluation(double Cost, double EtaS, double AddedDelayS, int LoadAtPickup, SimTime? PickupAt)
{
    public static CostEvaluation Ineligible(int load = 0) => new(double.PositiveInfinity, double.PositiveInfinity, 0, load, null);
}

/// <summary>
/// Cost-based dispatch (ADR-006): assign the request to the elevator with the lowest
///
///     cost = ETA + λ·ΔD + μ·L̂/C
///
/// <list type="bullet">
/// <item><b>ETA</b>: seconds from now until the request would be picked up, from a <see cref="StopProjection"/> of the
/// elevator with the request inserted, using the elevator's own scheduling policy and mechanics (per stop, so the
/// cost of a decision does not grow with the number of people queued).</item>
/// <item><b>ΔD</b>: total extra time every passenger already riding or assigned to that elevator would spend before
/// their drop-off, compared with the projection without the request. This is what stops a nearly-there elevator from
/// detouring its riders for a newcomer.</item>
/// <item><b>L̂/C</b>: predicted load at pickup (current riders + pickups before it − drop-offs before it) over capacity.
/// This is the load-balancing term (the spec's balanceLoad): prefer emptier elevators.</item>
/// </list>
/// Ineligible (cost ∞): maintenance, or the request would not be picked up within the projection horizon. In Defer
/// mode also: currently full, or at pickup the elevator would already be at the bypass threshold (L̂ ≥ threshold·C)
/// or the passenger would not fit (L̂ + load > C). In NextTrip mode (default, ADR-010) a full elevator stays a
/// candidate: its projected ETA includes waiting for its next drop-off cycle.
/// </summary>
public sealed class CostBasedDispatch(ISchedulingPolicy scheduling, ElevatorTiming timing, CostWeights? weights = null, string name = "cost") : IDispatchPolicy
{
    private readonly StopProjection _projection = new(scheduling, timing);
    private readonly CostWeights _weights = weights ?? CostWeights.Default;
    // Latest per elevator, keyed by the snapshot object itself. Not by (version, time): rebalancing evaluates derived
    // snapshots (the same elevator with one request removed) that share the version but not the content.
    private readonly Dictionary<int, (ElevatorSnapshot Snapshot, StopProjection.Outcome Result)> _baselines = [];

    public string Name => name;

    public DispatchChoice? Choose(DispatchContext context)
    {
        DispatchChoice? best = null;
        foreach (var s in context.Candidates.OrderBy(s => s.ElevatorId))
        {
            var eval = Evaluate(s, context.Request, context.Now);
            if (double.IsPositiveInfinity(eval.Cost)) continue;
            if (best is null || eval.Cost < best.Cost) best = new DispatchChoice(s.ElevatorId, eval.Cost, eval.PickupAt);
        }
        if (best is not null) return best;

        // NextTrip mode always names an elevator (ADR-010). If no projection reaches the pickup within its horizon
        // (a huge backlog), rank by Nearest's effective distance instead of deferring; no ETA, so no staleness check.
        if (context.Candidates.Any(c => c.CapacityMode == CapacityMode.NextTrip && c.State != ElevatorState.Maintenance)
            && _beyondHorizon.Choose(context) is { } fallback)
            return fallback with { Cost = double.MaxValue, Eta = null };
        return null;
    }

    private readonly NearestDispatch _beyondHorizon = new();

    public CostEvaluation Evaluate(ElevatorSnapshot s, Request r, SimTime now)
    {
        var defer = s.CapacityMode == CapacityMode.Defer;
        if (s.State == ElevatorState.Maintenance || (defer && s.IsFull)) return CostEvaluation.Ineligible(s.Load);

        var with = _projection.Project(s, r, now);
        if (with.PickupAt is not { } pickupAt) return CostEvaluation.Ineligible(with.LoadAtPickup);
        // Defer mode: same rule as its mechanics (at or above the threshold it hands pickups back; the passenger must
        // fit). NextTrip mode needs no such rule: the projection already puts a full elevator's pickup on its next
        // trip, so the ETA carries the wait (ADR-010).
        if (defer && (with.LoadAtPickup >= s.BypassThreshold * s.Capacity || with.LoadAtPickup + r.Load > s.Capacity))
            return CostEvaluation.Ineligible(with.LoadAtPickup);

        var addedDelayMs = StopProjection.AddedDelayMs(Baseline(s, now), with);

        var etaS = Math.Max(0, pickupAt.Ms - now.Ms) / 1000.0;
        var addedDelayS = addedDelayMs / 1000.0;
        var cost = etaS + _weights.Lambda * addedDelayS + _weights.Mu * with.LoadAtPickup / s.Capacity;
        return new CostEvaluation(cost, etaS, addedDelayS, with.LoadAtPickup, pickupAt);
    }

    /// <summary>
    /// Projection without the new request. With no input its future does not depend on when we look, so it is
    /// cached per snapshot; completions before <paramref name="now"/> appear in both projections and cancel out in ΔD.
    /// </summary>
    private StopProjection.Outcome Baseline(ElevatorSnapshot s, SimTime now)
    {
        // One entry per elevator (its latest snapshot): bounded memory however long the system runs.
        if (_baselines.TryGetValue(s.ElevatorId, out var cached) && ReferenceEquals(cached.Snapshot, s)) return cached.Result;
        var result = _projection.Project(s, null, now);
        _baselines[s.ElevatorId] = (s, result);
        return result;
    }
}
