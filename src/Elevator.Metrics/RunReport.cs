namespace Elevator.Metrics;

/// <summary>Summary statistics in sim seconds. Null values mean "no samples".</summary>
public sealed record DurationStats(int Count, double? Avg, double? P95, double? Max)
{
    public static DurationStats From(IReadOnlyCollection<double> seconds) =>
        new(seconds.Count,
            seconds.Count == 0 ? null : seconds.Average(),
            Percentile.NearestRank(seconds, 95),
            seconds.Count == 0 ? null : seconds.Max());
}

/// <summary>
/// The run report. All durations are sim seconds (never wall clock).
/// Wait = first press to boarding; ride = boarding to alighting; journey = wait + ride. Stats cover delivered
/// passengers only; anyone not delivered at the end is counted in <see cref="Unserved"/> instead.
/// <see cref="WaitBreakdown"/> is the mean seconds per delivered passenger in each bucket; it sums to the mean wait.
/// </summary>
public sealed record RunReport(
    string Scenario,
    string Scheduling,
    int Seed,
    int Passengers,
    int Delivered,
    int Unserved,
    int RejectedRequests,
    DurationStats Wait,
    DurationStats Ride,
    int FloorsTravelled,
    int Stops,
    double SimTimeS)
{
    public string Dispatch { get; init; } = "none";

    public int Elevators { get; init; } = 1;

    public DurationStats Journey { get; init; } = DurationStats.From([]);

    public int LeftBehind { get; init; }

    public int Reassignments { get; init; }

    public int Rejections { get; init; }

    public IReadOnlyDictionary<WaitBucket, double> WaitBreakdown { get; init; } = new Dictionary<WaitBucket, double>();
}
