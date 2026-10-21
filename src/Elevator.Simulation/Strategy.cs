namespace Elevator.Simulation;

using Elevator.Domain;

/// <summary>
/// A point in the experiment grid: scheduling × dispatch (× optional rebalancing), optionally overriding the
/// building's capacity mode (to compare NextTrip with Defer on the same seeds).
/// </summary>
public sealed record Strategy(string Scheduling, string Dispatch, bool Rebalance = false, CapacityMode? Capacity = null)
{
    public override string ToString() =>
        $"{Scheduling}+{Dispatch}{(Rebalance ? "+rebalance" : "")}{(Capacity is { } c ? "/" + CapacityModes.Name(c) : "")}";

    /// <summary>Parses "look+cost", "look+cost+rebalance", optionally followed by "/defer" or "/next-trip".</summary>
    public static Strategy Parse(string text)
    {
        var (main, mode) = text.ToLowerInvariant().Split('/') switch
        {
            [var m] => (m, (CapacityMode?)null),
            [var m, var c] => (m, CapacityModes.Parse(c)),
            _ => throw new FormatException($"strategy '{text}' must look like scheduling+dispatch[+rebalance][/capacity-mode]"),
        };
        var parts = main.Split('+', StringSplitOptions.TrimEntries);
        return parts switch
        {
            [var s, var d] => new Strategy(s, d, false, mode),
            [var s, var d, "rebalance"] => new Strategy(s, d, true, mode),
            _ => throw new FormatException($"strategy '{text}' must look like scheduling+dispatch[+rebalance][/capacity-mode]"),
        };
    }
}

public static class CapacityModes
{
    public static string Name(CapacityMode mode) => mode == CapacityMode.NextTrip ? "next-trip" : "defer";

    public static CapacityMode Parse(string text) => text.ToLowerInvariant() switch
    {
        "next-trip" or "nexttrip" => CapacityMode.NextTrip,
        "defer" => CapacityMode.Defer,
        _ => throw new FormatException($"capacity mode '{text}' must be next-trip or defer"),
    };
}
