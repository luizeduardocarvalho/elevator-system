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
    IReadOnlyDictionary<WaitBucket, double> Buckets);

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
                    Enum.GetValues<WaitBucket>().ToDictionary(b => b, b => bySeed.Values.Average(r => r.WaitBreakdown.GetValueOrDefault(b)))));
            }
        }
        return rows;
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
        }
        return sb.ToString();
    }

    public static string BucketName(WaitBucket b) => JsonNamingPolicy.SnakeCaseLower.ConvertName(b.ToString());

    private static string Delta(Estimate? e) => e is null ? "–" : e.Format() + (e.ExcludesZero ? " ✱" : "");
}
