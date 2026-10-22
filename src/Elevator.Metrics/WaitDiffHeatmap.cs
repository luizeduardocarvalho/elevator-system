using Elevator.Domain.Events;

namespace Elevator.Metrics;

/// <summary>
/// The difference map: strategy A's wait minus strategy B's, by origin floor × call-time bin, on the same passengers
/// (common random numbers). Negative means A waits less.
/// <list type="bullet">
/// <item><b>Paired.</b> A cell groups passengers by where and when they called, which does not depend on the strategy,
/// so it holds the same people in both runs. With <see cref="HeatStat.Avg"/> a cell is the mean over those people of
/// (wait under A − wait under B): a paired difference, not a difference of two unrelated averages.</item>
/// <item><b>Only people who boarded in both runs</b> are paired; the others are counted in <see cref="Excluded"/>.</item>
/// <item><b>P95</b> is p95(A) − p95(B) over the paired people of the cell.</item>
/// <item>Refuses runs whose passengers differ (id, origin, destination or call time): that would not be a paired comparison.</item>
/// </list>
/// </summary>
public sealed class WaitDiffHeatmap : IHeatGrid
{
    private readonly List<(double A, double B)>[,] _pairs;

    private WaitDiffHeatmap(int floors, int bins, long binMs, HeatStat stat, List<(double, double)>[,] pairs, int passengers, int paired)
    {
        Floors = floors;
        Bins = bins;
        BinMs = binMs;
        Stat = stat;
        _pairs = pairs;
        Passengers = passengers;
        Paired = paired;
    }

    public int Floors { get; }

    public int Bins { get; }

    public long BinMs { get; }

    public HeatStat Stat { get; }

    public int Passengers { get; }

    /// <summary>Passengers who boarded in both runs: the only ones in any cell.</summary>
    public int Paired { get; }

    public int Excluded => Passengers - Paired;

    public double? Overall => RangeStat(0, Bins);

    public static WaitDiffHeatmap FromEvents(IEnumerable<DomainEvent> a, IEnumerable<DomainEvent> b, double binS, HeatStat stat = HeatStat.Avg, double? spanS = null)
    {
        var listA = a as IReadOnlyList<DomainEvent> ?? a.ToList();
        var floors = listA.OfType<RunStarted>().FirstOrDefault()?.Floors
                     ?? throw new ArgumentException("log A has no RunStarted event", nameof(a));
        return Build(PassengerLifecycle.Analyze(listA), PassengerLifecycle.Analyze(b), floors, binS, stat, spanS);
    }

    public static WaitDiffHeatmap Build(IReadOnlyList<PassengerRecord> a, IReadOnlyList<PassengerRecord> b, int floors, double binS,
        HeatStat stat = HeatStat.Avg, double? spanS = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binS);
        var byId = b.ToDictionary(r => r.Id);
        if (a.Count != b.Count || a.Any(r => !byId.TryGetValue(r.Id, out var o) || (o.From, o.To, o.CalledAt) != (r.From, r.To, r.CalledAt)))
            throw new ArgumentException("the two runs have different passengers: not a paired comparison (use the same scenario and seed)");

        var binMs = (long)Math.Round(binS * 1000);
        var lastCall = a.Count == 0 ? -1 : a.Max(r => r.CalledAt.Ms);
        var bins = (int)Math.Max(lastCall < 0 ? 0 : lastCall / binMs + 1, spanS is { } span ? Math.Ceiling(span * 1000 / binMs) : 0);
        var pairs = new List<(double, double)>[floors, bins];
        for (var f = 0; f < floors; f++)
            for (var k = 0; k < bins; k++)
                pairs[f, k] = [];

        var paired = 0;
        foreach (var r in a)
        {
            if ((r.WaitS, byId[r.Id].WaitS) is not ({ } waitA, { } waitB)) continue;
            pairs[r.From - 1, (int)(r.CalledAt.Ms / binMs)].Add((waitA, waitB));
            paired++;
        }
        return new WaitDiffHeatmap(floors, bins, binMs, stat, pairs, a.Count, paired);
    }

    public double? Cell(int floor, int bin) => Of(_pairs[floor - 1, bin]);

    public int Count(int floor, int bin) => _pairs[floor - 1, bin].Count;

    public double? FloorStat(int floor) => RegionStat(floor, floor, 0, Bins);

    public double? BinStat(int bin) => RangeStat(bin, bin + 1);

    public double? RangeStat(int fromBin, int toBin) => RegionStat(1, Floors, fromBin, toBin);

    public double? RegionStat(int fromFloor, int toFloor, int fromBin, int toBin) =>
        Of(Enumerable.Range(fromBin, Math.Max(0, Math.Min(toBin, Bins) - fromBin))
            .SelectMany(k => Enumerable.Range(fromFloor - 1, toFloor - fromFloor + 1).SelectMany(f => _pairs[f, k])));

    private double? Of(IEnumerable<(double A, double B)> pairs)
    {
        var list = pairs as IReadOnlyCollection<(double A, double B)> ?? pairs.ToList();
        if (list.Count == 0) return null;
        return Stat == HeatStat.Avg
            ? list.Average(p => p.A - p.B)
            : Percentile.NearestRank(list.Select(p => p.A), 95) - Percentile.NearestRank(list.Select(p => p.B), 95);
    }
}
