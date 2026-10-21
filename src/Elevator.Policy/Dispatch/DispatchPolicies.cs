using Elevator.Domain;

namespace Elevator.Policy.Dispatch;

/// <summary>Every dispatch strategy stays selectable by name forever (<c>--dispatch</c>).</summary>
public static class DispatchPolicies
{
    private static readonly Dictionary<string, Func<ISchedulingPolicy, ElevatorTiming, IDispatchPolicy>> Factories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["roundrobin"] = (_, _) => new RoundRobinDispatch(),
            ["nearest"] = (_, _) => new NearestDispatch(),
        };

    public static IReadOnlyCollection<string> Names => Factories.Keys;

    /// <param name="scheduling">The elevators' scheduling policy (cost-based dispatch projects routes with it).</param>
    public static IDispatchPolicy Create(string name, ISchedulingPolicy scheduling, ElevatorTiming timing) =>
        Factories.TryGetValue(name, out var factory)
            ? factory(scheduling, timing)
            : throw new ArgumentException($"unknown dispatch policy '{name}' (known: {string.Join(", ", Names)})", nameof(name));
}
