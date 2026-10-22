using System.Collections.Immutable;

namespace Elevator.Domain;

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
