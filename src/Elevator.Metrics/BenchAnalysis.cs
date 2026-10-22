using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Elevator.Metrics;

/// <summary>One deterministic run of the bench grid.</summary>
public sealed record BenchRun(string Scenario, string Strategy, int Seed, RunReport Report);

/// <summary>A strategy's results on one scenario across seeds; deltas are paired against the baseline.</summary>
public sealed record BenchRow(
    string Scenario,
    string Strategy,
    string Role,
    Estimate WaitAvg,
    Estimate WaitP95,
    Estimate WaitMax,
    Estimate JourneyAvg,
    Estimate LeftBehind,
    Estimate ServedPct,
    Estimate? DeltaWaitAvg,
    Estimate? DeltaWaitP95,
    Estimate? DeltaJourneyAvg,
    IReadOnlyDictionary<WaitBucket, double> Buckets)
{
    /// <summary>Operations means per run (ADR-013); null when no run of this row had any operational activity.</summary>
    public OpsSummary? Ops { get; init; }
}

/// <summary>Operations metrics averaged over a row's runs (ADR-013). Penalty = reassignment + blocked bucket seconds per run.</summary>
public sealed record OpsSummary(double MaintenanceS, double EmergencyStops, double EmergencyS, double Faults, double? DetectS, double? RecoverS,
    double Releases, double PenaltyS, double Refused);

public static class BenchAnalysis
{
    public static IReadOnlyList<BenchRow> Summarize(IEnumerable<BenchRun> runs, string baseline, IReadOnlyCollection<string> lowerBounds)
    {
        var rows = new List<BenchRow>();
        foreach (var scenario in runs.GroupBy(r => r.Scenario))
        {
            var byStrategy = scenario.GroupBy(r => r.Strategy).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Seed, r => r.Report));
            var reference = byStrategy.GetValueOrDefault(baseline);

            foreach (var (strategy, bySeed) in byStrategy)
            {
                Estimate Of(Func<RunReport, double> metric) => Stats.MeanCi(bySeed.Values.Select(metric).ToList());
                Estimate? Delta(Func<RunReport, double> metric) =>
                    reference is null || strategy == baseline
                        ? null
                        : Stats.PairedDifference(bySeed.ToDictionary(kv => kv.Key, kv => metric(kv.Value)),
                                                 reference.ToDictionary(kv => kv.Key, kv => metric(kv.Value)));

                rows.Add(new BenchRow(
                    scenario.Key,
                    strategy,
                    strategy == baseline ? "baseline" : lowerBounds.Contains(strategy) ? "lower bound" : "",
                    Of(r => r.Wait.Avg ?? double.NaN),
                    Of(r => r.Wait.P95 ?? double.NaN),
                    Of(r => r.Wait.Max ?? double.NaN),
                    Of(r => r.Journey.Avg ?? double.NaN),
                    Of(r => r.LeftBehind),
                    Of(r => 100.0 * r.Delivered / Math.Max(1, r.Passengers)),
                    Delta(r => r.Wait.Avg ?? double.NaN),
                    Delta(r => r.Wait.P95 ?? double.NaN),
                    Delta(r => r.Journey.Avg ?? double.NaN),
                    Enum.GetValues<WaitBucket>().ToDictionary(b => b, b => bySeed.Values.Average(r => r.WaitBreakdown.GetValueOrDefault(b))))
                {
                    Ops = bySeed.Values.Any(r => r.Ops.Any) ? Summarize(bySeed.Values.Select(r => r.Ops).ToList()) : null,
                });
            }
        }
        return rows;
    }

    private static OpsSummary Summarize(IReadOnlyList<OpsReport> runs)
    {
        static double? AvgOf(IEnumerable<double?> values) => values.OfType<double>().ToList() is { Count: > 0 } v ? v.Average() : null;
        return new OpsSummary(
            runs.Average(o => o.MaintenanceS), runs.Average(o => o.EmergencyStops), runs.Average(o => o.EmergencyS), runs.Average(o => o.Faults),
            AvgOf(runs.Select(o => o.TimeToDetect.Avg)), AvgOf(runs.Select(o => o.TimeToRecover.Avg)), runs.Average(o => o.OutOfServiceReleases),
            runs.Average(o => o.ReassignmentWaitS + o.BlockedWaitS), runs.Average(o => o.RefusedByPolicy));
    }

    public static string ToMarkdown(IReadOnlyList<BenchRow> rows, string title, IReadOnlyDictionary<string, string> meta)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        foreach (var (k, v) in meta) sb.AppendLine($"- **{k}:** {v}");
        sb.AppendLine()
          .AppendLine("All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).")
          .AppendLine("Δ columns are **paired per-seed differences** against the baseline (common random numbers: every")
          .AppendLine("strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.")
          .AppendLine();

        foreach (var scenario in rows.GroupBy(r => r.Scenario))
        {
            sb.AppendLine($"## {scenario.Key}").AppendLine()
              .AppendLine("| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |")
              .AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in scenario)
            {
                var name = r.Role.Length > 0 ? $"{r.Strategy} ({r.Role})" : r.Strategy;
                sb.AppendLine($"| {name} | {r.WaitAvg.Format()} | {r.WaitP95.Format()} | {r.WaitMax.Format()} | {r.JourneyAvg.Format()} | " +
                              $"{r.LeftBehind.Format()} | {r.ServedPct.Format()} | {Delta(r.DeltaWaitAvg)} | {Delta(r.DeltaWaitP95)} | {Delta(r.DeltaJourneyAvg)} |");
            }

            sb.AppendLine().AppendLine("Where the average wait goes (mean seconds per passenger; sums to the mean wait):").AppendLine();
            var buckets = Enum.GetValues<WaitBucket>();
            sb.AppendLine("| Strategy | " + string.Join(" | ", buckets.Select(BucketName)) + " |")
              .AppendLine("|---|" + string.Concat(buckets.Select(_ => "---|")));
            foreach (var r in scenario)
                sb.AppendLine($"| {r.Strategy} | " + string.Join(" | ", buckets.Select(b => r.Buckets[b].ToString("0.0", CultureInfo.InvariantCulture))) + " |");
            sb.AppendLine();

            if (scenario.Any(r => r.Ops is not null))
            {
                static string F(double? v) => v is { } x ? x.ToString("0.0", CultureInfo.InvariantCulture) : "–";
                sb.AppendLine("Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):").AppendLine()
                  .AppendLine("| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |")
                  .AppendLine("|---|---|---|---|---|---|---|---|---|---|");
                foreach (var r in scenario)
                {
                    var o = r.Ops ?? new OpsSummary(0, 0, 0, 0, null, null, 0, 0, 0);
                    sb.AppendLine($"| {r.Strategy} | {F(o.MaintenanceS)} | {F(o.EmergencyStops)} | {F(o.EmergencyS)} | {F(o.Faults)} | {F(o.DetectS)} | " +
                                  $"{F(o.RecoverS)} | {F(o.Releases)} | {F(o.PenaltyS)} | {F(o.Refused)} |");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public static string BucketName(WaitBucket b) => JsonNamingPolicy.SnakeCaseLower.ConvertName(b.ToString());

    private static string Delta(Estimate? e) => e is null ? "–" : e.Format() + (e.ExcludesZero ? " ✱" : "");
}
