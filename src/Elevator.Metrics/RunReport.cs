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
/// The basic v1 report. All durations are sim seconds (never wall clock).
/// Wait = hall call to boarding; ride = boarding to alighting. Stats cover delivered passengers only;
/// anyone still waiting at the end is counted in <see cref="Unserved"/> instead.
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
    double SimTimeS);
