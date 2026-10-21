namespace Elevator.Simulation;

/// <summary>A point in the experiment grid: scheduling × dispatch (× optional rebalancing).</summary>
public sealed record Strategy(string Scheduling, string Dispatch, bool Rebalance = false)
{
    public override string ToString() => $"{Scheduling}+{Dispatch}{(Rebalance ? "+rebalance" : "")}";

    /// <summary>Parses "look+cost" or "look+cost+rebalance".</summary>
    public static Strategy Parse(string text)
    {
        var parts = text.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries);
        return parts switch
        {
            [var s, var d] => new Strategy(s, d),
            [var s, var d, "rebalance"] => new Strategy(s, d, true),
            _ => throw new FormatException($"strategy '{text}' must look like scheduling+dispatch[+rebalance]"),
        };
    }
}
