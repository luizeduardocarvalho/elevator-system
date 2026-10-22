using Elevator.Domain;

namespace Elevator.Policy.Dispatch;

/// <param name="Lambda">Weight of the delay the new pickup adds to people already assigned or riding (seconds per second).</param>
/// <param name="Mu">Seconds of penalty for an elevator that would be completely full at pickup (scaled by L̂/C).</param>
public sealed record CostWeights(double Lambda = 0.5, double Mu = 10.0)
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
/// Ineligible (cost ∞): maintenance, or the request would not be picked up within the projection horizon. A full
/// elevator stays a candidate (ADR-010): its projected ETA includes waiting for its next drop-off cycle.
/// </summary>
public sealed class CostBasedDispatch(ISchedulingPolicy scheduling, ElevatorTiming timing, CostWeights? weights = null, string name = "cost") : IDispatchPolicy
{
    private readonly StopProjection _projection = new(scheduling, timing);
    private readonly CostWeights _weights = weights ?? CostWeights.Default;
    private readonly Dictionary<int, (ElevatorSnapshot Snapshot, StopProjection.Outcome Result)> _baselines = [];

    public string Name => name;

    public DispatchChoice? Choose(DispatchContext context)
    {
        DispatchChoice? best = null;
        var explained = context.Explain ? new List<Domain.Events.CandidateCost>() : null;
        foreach (var s in context.Candidates.OrderBy(s => s.ElevatorId))
        {
            var eval = Evaluate(s, context.Request, context.Now, context.VipPriority);
            explained?.Add(double.IsPositiveInfinity(eval.Cost)
                ? new Domain.Events.CandidateCost(s.ElevatorId, null, "ineligible")
                : new Domain.Events.CandidateCost(s.ElevatorId, Math.Round(eval.Cost, 3), $"eta {eval.EtaS:0.#}s + {(context.VipPriority && context.Request.Vip ? 0 : _weights.Lambda)}·{eval.AddedDelayS:0.#}s + {_weights.Mu}·{eval.LoadAtPickup}/{s.Capacity}"));
            if (double.IsPositiveInfinity(eval.Cost)) continue;
            if (best is null || eval.Cost < best.Cost) best = new DispatchChoice(s.ElevatorId, eval.Cost, eval.PickupAt);
        }
        if (best is not null) return explained is null ? best : best with { Candidates = explained };

        if (context.Candidates.Any(c => c.State != ElevatorState.Maintenance)
            && _beyondHorizon.Choose(context) is { } fallback)
            return fallback with { Cost = double.MaxValue, Eta = null, Candidates = explained };
        return null;
    }

    private readonly NearestDispatch _beyondHorizon = new();

    /// <param name="vipPriority">VIP priority (ADR-015): a VIP request is not charged λ·ΔD, the delay it adds to people
    /// already riding or assigned, so it takes the fastest pickup. ΔD is still computed and reported.</param>
    public CostEvaluation Evaluate(ElevatorSnapshot s, Request r, SimTime now, bool vipPriority = false)
    {
        if (s.State == ElevatorState.Maintenance) return CostEvaluation.Ineligible(s.Load);

        var with = _projection.Project(s, r, now);
        if (with.PickupAt is not { } pickupAt) return CostEvaluation.Ineligible(with.LoadAtPickup);

        var addedDelayMs = StopProjection.AddedDelayMs(Baseline(s, now), with);

        var etaS = Math.Max(0, pickupAt.Ms - now.Ms) / 1000.0;
        var addedDelayS = addedDelayMs / 1000.0;
        var lambda = vipPriority && r.Vip ? 0 : _weights.Lambda;
        var cost = etaS + lambda * addedDelayS + _weights.Mu * with.LoadAtPickup / s.Capacity;
        return new CostEvaluation(cost, etaS, addedDelayS, with.LoadAtPickup, pickupAt);
    }

    private StopProjection.Outcome Baseline(ElevatorSnapshot s, SimTime now)
    {
        if (_baselines.TryGetValue(s.ElevatorId, out var cached) && ReferenceEquals(cached.Snapshot, s)) return cached.Result;
        var result = _projection.Project(s, null, now);
        _baselines[s.ElevatorId] = (s, result);
        return result;
    }
}
