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
/// <item><b>ETA</b>: seconds from now until the request would be picked up, from a <see cref="RouteProjection"/> of the
/// elevator with the request inserted, using the elevator's own scheduling policy.</item>
/// <item><b>ΔD</b>: total extra time every passenger already riding or assigned to that elevator would spend before
/// their drop-off, compared with the projection without the request. This is what stops a nearly-there elevator from
/// detouring its riders for a newcomer.</item>
/// <item><b>L̂/C</b>: predicted load at pickup (current riders + pickups before it − drop-offs before it) over capacity.
/// This is the load-balancing term (the spec's balanceLoad): prefer emptier elevators.</item>
/// </list>
/// Ineligible (cost ∞): maintenance, currently full, the request would not be picked up, or at pickup the
/// elevator would already be at the bypass threshold (L̂ ≥ threshold·C) or the passenger would not fit (L̂ + load > C).
/// </summary>
public sealed class CostBasedDispatch(ISchedulingPolicy scheduling, ElevatorTiming timing, CostWeights? weights = null, string name = "cost") : IDispatchPolicy
{
    private readonly RouteProjection _projection = new(scheduling, timing);
    private readonly CostWeights _weights = weights ?? CostWeights.Default;
    private readonly Dictionary<int, (long Version, long AtMs, RouteProjection.Result Result)> _baselines = []; // latest per elevator

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
        return best;
    }

    public CostEvaluation Evaluate(ElevatorSnapshot s, Request r, SimTime now)
    {
        if (s.State == ElevatorState.Maintenance || s.IsFull) return CostEvaluation.Ineligible(s.Load);

        var with = _projection.Project(s, r, now);
        if (with.PickupAt is not { } pickupAt) return CostEvaluation.Ineligible(with.LoadAtPickup);
        // Same rule as the mechanics: at or above the threshold it bypasses pickups; and the passenger must fit.
        if (with.LoadAtPickup >= s.BypassThreshold * s.Capacity || with.LoadAtPickup + r.Load > s.Capacity)
            return CostEvaluation.Ineligible(with.LoadAtPickup);

        var without = Baseline(s, now);
        var addedDelayMs = 0L;
        foreach (var (id, completion) in without.Completions)
            addedDelayMs += (with.Completions.TryGetValue(id, out var c) ? c : with.Horizon).Ms - completion.Ms;

        var etaS = Math.Max(0, pickupAt.Ms - now.Ms) / 1000.0;
        var addedDelayS = addedDelayMs / 1000.0;
        var cost = etaS + _weights.Lambda * addedDelayS + _weights.Mu * with.LoadAtPickup / s.Capacity;
        return new CostEvaluation(cost, etaS, addedDelayS, with.LoadAtPickup, pickupAt);
    }

    /// <summary>
    /// Projection without the new request. With no input its future does not depend on when we look, so it is
    /// cached per snapshot version; completions before <paramref name="now"/> appear in both projections and cancel out in ΔD.
    /// </summary>
    private RouteProjection.Result Baseline(ElevatorSnapshot s, SimTime now)
    {
        // One entry per elevator (its latest snapshot): bounded memory however long the system runs.
        if (_baselines.TryGetValue(s.ElevatorId, out var cached) && cached.Version == s.Version && cached.AtMs == s.At.Ms) return cached.Result;
        var result = _projection.Project(s, null, now);
        _baselines[s.ElevatorId] = (s.Version, s.At.Ms, result);
        return result;
    }
}
