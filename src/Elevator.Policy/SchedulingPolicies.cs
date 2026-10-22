using Elevator.Domain;

namespace Elevator.Policy;

public static class SchedulingPolicies
{
    private static readonly Dictionary<string, Func<ISchedulingPolicy>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fifo"] = () => new FifoScheduling(),
        ["look"] = () => new LookScheduling(),
    };

    public static IReadOnlyCollection<string> Names => Factories.Keys;

    public static ISchedulingPolicy Create(string name) =>
        Factories.TryGetValue(name, out var factory)
            ? factory()
            : throw new ArgumentException($"unknown scheduling policy '{name}' (known: {string.Join(", ", Names)})", nameof(name));
}
