namespace Elevator.Metrics;

public static class Percentile
{
    /// <summary>
    /// Nearest-rank percentile: the smallest sample such that at least p% of samples are ≤ it,
    /// i.e. sorted[ceil(p/100 · n) − 1]. Always an observed value (no interpolation).
    /// Null for an empty sample.
    /// </summary>
    public static double? NearestRank(IEnumerable<double> samples, double p)
    {
        if (p is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(p), "p must be in (0, 100]");
        var sorted = samples.Order().ToArray();
        if (sorted.Length == 0) return null;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length);
        return sorted[rank - 1];
    }
}
