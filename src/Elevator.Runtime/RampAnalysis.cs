namespace Elevator.Runtime;

/// <summary>
/// Raw timestamps of one ramp step (Stopwatch ticks), as the open-loop generator recorded them (ADR-016).
/// </summary>
/// <param name="Intended">When request i was scheduled to be sent: t0 + i / rate.</param>
/// <param name="Sent">When the generator actually submitted it.</param>
/// <param name="Completed">When its acceptance reached the runtime; 0 if it never did before the drain ended.</param>
/// <param name="InFlightAverage">Requests in flight, sampled about every millisecond during the window.</param>
/// <param name="Gc">Collections per generation (0, 1, 2) during the window.</param>
public sealed record StepSamples(
    double OfferedPerSecond,
    long Frequency,
    long WindowStart,
    long WindowEnd,
    long[] Intended,
    long[] Sent,
    long[] Completed,
    double InFlightAverage,
    long Proposals,
    long FallbackProposals,
    (int Gen0, int Gen1, int Gen2) Gc);

/// <summary>One row of the ramp table. Latencies in wall-clock ms, from the intended send time.</summary>
public sealed record RampStep(
    double Offered,
    double Achieved,
    int Measured,
    int Unfinished,
    double P50Ms,
    double P99Ms,
    double P999Ms,
    double MaxMs,
    double InFlightSampled,
    double LittleL,
    double LittleDeviationPct,
    double FallbackPct,
    (int Gen0, int Gen1, int Gen2) Gc,
    double LagP99Ms,
    double LagMaxMs)
{
    /// <summary>The generator kept to its schedule: p99 of (sent − intended) within 1 ms. Otherwise the offered rate was not offered.</summary>
    public bool Valid => LagP99Ms <= RampAnalysis.MaxLagMs;

    /// <summary>The system did not keep up: achieved below 99 % of offered. The ramp stops after this step.</summary>
    public bool Saturated => Achieved < RampAnalysis.KeepUpFraction * Offered;

    /// <summary>The sampled in-flight average agrees with λ·W within 5 %.</summary>
    public bool LittleHolds => LittleDeviationPct <= RampAnalysis.LittleTolerancePct;
}

/// <summary>
/// Turns one step's timestamps into its row (ADR-016). Open loop: request i is due at t0 + i/rate whatever happened
/// to the ones before, and latency is measured from that intended time, so a stalled system (or a stalled generator)
/// shows up as latency instead of quietly lowering the load (coordinated omission).
/// <list type="bullet">
/// <item><b>Measured requests:</b> those intended inside the window (warm-up excluded).</item>
/// <item><b>Achieved:</b> acceptances that happened inside the window, per second, whichever request they belong to.</item>
/// <item><b>Unfinished:</b> measured requests never accepted; their latency counts as (drain end − intended), a lower bound.</item>
/// <item><b>Little's law:</b> L = λ·W with λ = achieved and W = mean(accepted − sent) of the measured requests,
/// compared with the in-flight count sampled independently every millisecond.</item>
/// </list>
/// </summary>
public static class RampAnalysis
{
    public const double MaxLagMs = 1.0;
    public const double KeepUpFraction = 0.99;
    public const double LittleTolerancePct = 5.0;

    public static RampStep Analyze(StepSamples s)
    {
        double Ms(long ticks) => ticks * 1000.0 / s.Frequency;
        var windowS = (s.WindowEnd - s.WindowStart) / (double)s.Frequency;
        var drainEnd = Math.Max(s.WindowEnd, s.Completed.Max());

        var latencies = new List<double>();
        var lags = new List<double>();
        double sojournSum = 0;
        var sojourns = 0;
        var unfinished = 0;
        var achieved = 0;
        for (var i = 0; i < s.Intended.Length; i++)
        {
            if (s.Completed[i] is var done and > 0 && done >= s.WindowStart && done < s.WindowEnd) achieved++;
            if (s.Intended[i] < s.WindowStart || s.Intended[i] >= s.WindowEnd) continue;
            lags.Add(Ms(s.Sent[i] - s.Intended[i]));
            if (s.Completed[i] > 0)
            {
                latencies.Add(Ms(s.Completed[i] - s.Intended[i]));
                sojournSum += Ms(s.Completed[i] - s.Sent[i]) / 1000.0;
                sojourns++;
            }
            else
            {
                unfinished++;
                latencies.Add(Ms(drainEnd - s.Intended[i]));
            }
        }

        latencies.Sort();
        lags.Sort();
        var achievedPerS = achieved / windowS;
        var little = sojourns == 0 ? 0 : achievedPerS * (sojournSum / sojourns);
        return new RampStep(
            s.OfferedPerSecond,
            achievedPerS,
            latencies.Count,
            unfinished,
            Rank(latencies, 50),
            Rank(latencies, 99),
            Rank(latencies, 99.9),
            latencies.Count == 0 ? double.NaN : latencies[^1],
            s.InFlightAverage,
            little,
            little == 0 ? double.NaN : 100 * Math.Abs(s.InFlightAverage - little) / little,
            s.Proposals == 0 ? 0 : 100.0 * s.FallbackProposals / s.Proposals,
            s.Gc,
            Rank(lags, 99),
            lags.Count == 0 ? double.NaN : lags[^1]);
    }

    /// <summary>Nearest rank on a sorted list (an observed value, as everywhere in this project).</summary>
    private static double Rank(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var rank = (int)Math.Ceiling(p / 100 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }
}
