using System.Collections.Immutable;

namespace Elevator.Domain;

/// <summary>
/// How many people (and how much load) are headed to each destination floor, for a group of requests: the riders of
/// an elevator, or the requests sharing one pickup stop. Maintained incrementally by the elevator (O(floors) per
/// change), so route projections can work per stop instead of per passenger (ADR-006).
/// </summary>
public sealed record DestinationTotals(ImmutableArray<int> Counts, ImmutableArray<int> Loads, int TotalCount, int TotalLoad)
{
    public static DestinationTotals Empty { get; } = new([], [], 0, 0);

    public int CountTo(int floor) => floor < Counts.Length ? Counts[floor] : 0;

    public int LoadTo(int floor) => floor < Loads.Length ? Loads[floor] : 0;

    /// <summary>Highest destination floor index stored (+1).</summary>
    public int Span => Counts.Length;

    public DestinationTotals Add(Request r) => Change(r, +1);

    public DestinationTotals Remove(Request r) => Change(r, -1);

    public static DestinationTotals Of(IEnumerable<Request> requests) => requests.Aggregate(Empty, (t, r) => t.Add(r));

    private DestinationTotals Change(Request r, int sign)
    {
        var size = Math.Max(Counts.Length, r.DestinationFloor + 1);
        var counts = new int[size];
        var loads = new int[size];
        Counts.CopyTo(counts);
        Loads.CopyTo(loads);
        counts[r.DestinationFloor] += sign;
        loads[r.DestinationFloor] += sign * r.Load;
        return new DestinationTotals([.. counts], [.. loads], TotalCount + sign, TotalLoad + sign * r.Load);
    }
}
