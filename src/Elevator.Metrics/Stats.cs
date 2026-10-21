using System.Globalization;

namespace Elevator.Metrics;

/// <summary>A mean with its 95% confidence half-width: the true mean lies in [Mean − HalfWidth, Mean + HalfWidth] with 95% confidence.</summary>
public sealed record Estimate(double Mean, double HalfWidth, int N)
{
    public bool ExcludesZero => !double.IsNaN(HalfWidth) && (Mean - HalfWidth > 0 || Mean + HalfWidth < 0);

    public string Format(string number = "0.0") =>
        double.IsNaN(HalfWidth)
            ? Mean.ToString(number, CultureInfo.InvariantCulture)
            : $"{Mean.ToString(number, CultureInfo.InvariantCulture)} ± {HalfWidth.ToString(number, CultureInfo.InvariantCulture)}";
}

public static class Stats
{
    /// <summary>
    /// Mean and 95% CI using Student's t: mean ± t(0.975, n−1) · s/√n, with s the sample standard deviation.
    /// Each sample is one seed's run, so samples are independent. The half-width is NaN with fewer than two samples.
    /// </summary>
    public static Estimate MeanCi(IReadOnlyCollection<double> samples)
    {
        var n = samples.Count;
        if (n == 0) return new Estimate(double.NaN, double.NaN, 0);
        var mean = samples.Average();
        if (n == 1) return new Estimate(mean, double.NaN, 1);
        var sd = Math.Sqrt(samples.Sum(x => (x - mean) * (x - mean)) / (n - 1));
        return new Estimate(mean, StudentT975(n - 1) * sd / Math.Sqrt(n), n);
    }

    /// <summary>
    /// Paired comparison under common random numbers: for every seed both strategies ran, d = strategy − baseline,
    /// then a CI on the mean of d. Pairing cancels the seed-to-seed variation in traffic, which is much larger than
    /// the strategy effect, so differences are detected with far fewer seeds than comparing two independent means.
    /// </summary>
    public static Estimate PairedDifference(IReadOnlyDictionary<int, double> strategyBySeed, IReadOnlyDictionary<int, double> baselineBySeed) =>
        MeanCi(strategyBySeed.Keys.Where(baselineBySeed.ContainsKey).Order().Select(s => strategyBySeed[s] - baselineBySeed[s]).ToList());

    // Two-sided 95% critical values of Student's t (0.975 quantile), from standard tables.
    private static readonly (int Df, double T)[] Table =
    [
        (1, 12.706), (2, 4.303), (3, 3.182), (4, 2.776), (5, 2.571), (6, 2.447), (7, 2.365), (8, 2.306), (9, 2.262),
        (10, 2.228), (11, 2.201), (12, 2.179), (13, 2.160), (14, 2.145), (15, 2.131), (16, 2.120), (17, 2.110),
        (18, 2.101), (19, 2.093), (20, 2.086), (21, 2.080), (22, 2.074), (23, 2.069), (24, 2.064), (25, 2.060),
        (26, 2.056), (27, 2.052), (28, 2.048), (29, 2.045), (30, 2.042), (40, 2.021), (60, 2.000), (120, 1.980),
    ];

    /// <summary>t(0.975, df) from the table, linear in 1/df between rows (the usual table interpolation), 1.960 as df → ∞.</summary>
    public static double StudentT975(int df)
    {
        if (df < 1) throw new ArgumentOutOfRangeException(nameof(df));
        for (var i = 0; i < Table.Length; i++)
        {
            if (Table[i].Df == df) return Table[i].T;
            if (Table[i].Df > df)
            {
                var (lo, hi) = (Table[i - 1], Table[i]);
                var f = (1.0 / lo.Df - 1.0 / df) / (1.0 / lo.Df - 1.0 / hi.Df);
                return lo.T + f * (hi.T - lo.T);
            }
        }
        var last = Table[^1];
        return 1.960 + (last.T - 1.960) * last.Df / df; // linear in 1/df towards the normal quantile
    }
}
