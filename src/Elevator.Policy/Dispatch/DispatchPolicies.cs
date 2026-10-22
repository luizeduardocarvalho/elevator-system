using Elevator.Domain;

namespace Elevator.Policy.Dispatch;

public static class DispatchPolicies
{
    private static readonly Dictionary<string, Func<ISchedulingPolicy, ElevatorTiming, IDispatchPolicy>> Factories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["roundrobin"] = (_, _) => new RoundRobinDispatch(),
            ["nearest"] = (_, _) => new NearestDispatch(),
            ["cost"] = (scheduling, timing) => new CostBasedDispatch(scheduling, timing),
        };

    public static IReadOnlyCollection<string> Names => Factories.Keys;

    public static bool IsKnown(string name)
    {
        try
        {
            Create(name, new FifoScheduling(), ElevatorTiming.Default);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <param name="name">A registered name, or "cost:λ:μ" for cost-based dispatch with explicit weights (used by bench sweeps).</param>
    /// <param name="scheduling">The elevators' scheduling policy (cost-based dispatch projects routes with it).</param>
    public static IDispatchPolicy Create(string name, ISchedulingPolicy scheduling, ElevatorTiming timing)
    {
        var parts = name.Split(':');
        if (parts is ["cost", var l, var m]
            && double.TryParse(l, System.Globalization.CultureInfo.InvariantCulture, out var lambda)
            && double.TryParse(m, System.Globalization.CultureInfo.InvariantCulture, out var mu)
            && lambda >= 0 && mu >= 0)
            return new CostBasedDispatch(scheduling, timing, new CostWeights(lambda, mu), name);

        return Factories.TryGetValue(name, out var factory)
            ? factory(scheduling, timing)
            : throw new ArgumentException($"unknown dispatch policy '{name}' (known: {string.Join(", ", Names)}, or cost:λ:μ)", nameof(name));
    }
}
