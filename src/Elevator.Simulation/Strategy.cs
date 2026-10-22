namespace Elevator.Simulation;

/// <summary>
/// A point in the experiment grid: which dispatch policy to run (LOOK scheduling and next-trip capacity are fixed;
/// ADR-018). A name from <c>DispatchPolicies</c>, or "cost:λ:μ" for explicit cost weights.
/// </summary>
public sealed record Strategy(string Dispatch)
{
    public override string ToString() => Dispatch;

    public static Strategy Parse(string text) =>
        text.Contains('+') || text.Contains('/')
            ? throw new FormatException($"strategy '{text}' must be a dispatch policy name (scheduling and capacity mode are fixed, ADR-018)")
            : new Strategy(text.Trim().ToLowerInvariant());
}
