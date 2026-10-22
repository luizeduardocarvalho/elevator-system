using Elevator.Domain.Events;

namespace Elevator.Metrics;

/// <summary>Which statistic a heatmap cell shows.</summary>
public enum HeatStat
{
    Avg,
    P95,
}

/// <summary>A floors × time-bins grid of wait statistics in sim seconds, as drawn by the heatmap view.</summary>
public interface IHeatGrid
{
    int Floors { get; }

    int Bins { get; }

    long BinMs { get; }

    HeatStat Stat { get; }

    /// <summary>The value for callers from <paramref name="floor"/> (1-based) in <paramref name="bin"/>; null when nobody called.</summary>
    double? Cell(int floor, int bin);

    int Count(int floor, int bin);

    double? FloorStat(int floor);

    double? BinStat(int bin);

    double? RangeStat(int fromBin, int toBin);

    double? RegionStat(int fromFloor, int toFloor, int fromBin, int toBin);

    double? Overall { get; }
}

/// <summary>
/// "The shape of a working day": wait (sim seconds) by origin floor × call-time bin, from the event log only.
/// <list type="bullet">
/// <item>A passenger counts once: on the floor they called from, in the bin of their call time (first press), with
/// their wait from call to boarding (the report's definition).</item>
/// <item>Passengers who never boarded are counted in <see cref="NotBoarded"/> and kept out of every cell, so a cell
/// never shows a short wait because the people in it were never picked up.</item>
/// <item>Cells with no callers have no value (null), never 0 s.</item>
/// <item>Margins pool the passengers (a floor's value is over all its passengers, not the mean of its cells), so a
/// busy bin weighs more than a quiet one, as in the report.</item>
/// </list>
/// </summary>
public sealed class WaitHeatmap : IHeatGrid
{
    private readonly List<double>[,] _waits;
    private readonly HeatStat _stat;

    private WaitHeatmap(int floors, int bins, long binMs, HeatStat stat, List<double>[,] waits, int passengers, int boarded)
    {
        Floors = floors;
        Bins = bins;
        BinMs = binMs;
        _stat = stat;
        _waits = waits;
        Passengers = passengers;
        Boarded = boarded;
    }

    public int Floors { get; }

    public int Bins { get; }

    public long BinMs { get; }

    public HeatStat Stat => _stat;

    public int Passengers { get; }

    public int Boarded { get; }

    public int NotBoarded => Passengers - Boarded;

    /// <summary>All boarded passengers.</summary>
    public double? Overall => RangeStat(0, Bins);

    public static WaitHeatmap FromEvents(IEnumerable<DomainEvent> events, double binS, HeatStat stat = HeatStat.Avg, double? spanS = null)
    {
        var list = events as IReadOnlyList<DomainEvent> ?? events.ToList();
        var floors = list.OfType<RunStarted>().FirstOrDefault()?.Floors
                     ?? throw new ArgumentException("the log has no RunStarted event", nameof(events));
        return Build(PassengerLifecycle.Analyze(list), floors, binS, stat, spanS);
    }

    public static WaitHeatmap Build(IReadOnlyList<PassengerRecord> records, int floors, double binS, HeatStat stat = HeatStat.Avg, double? spanS = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binS);
        var binMs = (long)Math.Round(binS * 1000);
        var lastCall = records.Count == 0 ? -1 : records.Max(r => r.CalledAt.Ms);
        var bins = (int)Math.Max(lastCall < 0 ? 0 : lastCall / binMs + 1, spanS is { } span ? Math.Ceiling(span * 1000 / binMs) : 0);

        var waits = new List<double>[floors, bins];
        for (var f = 0; f < floors; f++)
            for (var b = 0; b < bins; b++)
                waits[f, b] = [];

        var boarded = 0;
        foreach (var r in records)
        {
            if (r.WaitS is not { } wait) continue;
            waits[r.From - 1, (int)(r.CalledAt.Ms / binMs)].Add(wait);
            boarded++;
        }
        return new WaitHeatmap(floors, bins, binMs, stat, waits, records.Count, boarded);
    }

    /// <summary>The statistic for callers from <paramref name="floor"/> (1-based) in <paramref name="bin"/>; null when nobody called.</summary>
    public double? Cell(int floor, int bin) => Of(_waits[floor - 1, bin]);

    public int Count(int floor, int bin) => _waits[floor - 1, bin].Count;

    /// <summary>All boarded passengers who called from <paramref name="floor"/>, over the whole run.</summary>
    public double? FloorStat(int floor) => Of(Enumerable.Range(0, Bins).SelectMany(b => _waits[floor - 1, b]));

    /// <summary>All boarded passengers who called in <paramref name="bin"/>, from any floor.</summary>
    public double? BinStat(int bin) => RangeStat(bin, bin + 1);

    /// <summary>All boarded passengers who called in bins [<paramref name="fromBin"/>, <paramref name="toBin"/>), from any floor.</summary>
    public double? RangeStat(int fromBin, int toBin) => RegionStat(1, Floors, fromBin, toBin);

    /// <summary>Boarded passengers who called from floors [<paramref name="fromFloor"/>, <paramref name="toFloor"/>] (inclusive)
    /// in bins [<paramref name="fromBin"/>, <paramref name="toBin"/>).</summary>
    public double? RegionStat(int fromFloor, int toFloor, int fromBin, int toBin) =>
        Of(Enumerable.Range(fromBin, Math.Max(0, Math.Min(toBin, Bins) - fromBin))
            .SelectMany(b => Enumerable.Range(fromFloor - 1, toFloor - fromFloor + 1).SelectMany(f => _waits[f, b])));

    private double? Of(IEnumerable<double> waits)
    {
        var list = waits as IReadOnlyCollection<double> ?? waits.ToList();
        if (list.Count == 0) return null;
        return _stat == HeatStat.Avg ? list.Average() : Percentile.NearestRank(list, 95);
    }
}
