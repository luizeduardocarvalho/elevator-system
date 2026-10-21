using System.Diagnostics;
using System.Globalization;

namespace Elevator.Metrics;

/// <summary>
/// Real elapsed time (wall clock), e.g. assignment latency. Deliberately a different type from SimTime so the two
/// kinds of time can never be mixed (ADR-008).
/// </summary>
public readonly record struct WallDuration(long StopwatchTicks) : IComparable<WallDuration>
{
    public static WallDuration Since(long startTimestamp) => new(Stopwatch.GetTimestamp() - startTimestamp);

    public double Milliseconds => StopwatchTicks * 1000.0 / Stopwatch.Frequency;

    public double Microseconds => StopwatchTicks * 1_000_000.0 / Stopwatch.Frequency;

    public int CompareTo(WallDuration other) => StopwatchTicks.CompareTo(other.StopwatchTicks);

    public override string ToString() => Milliseconds.ToString("0.000", CultureInfo.InvariantCulture) + " ms (wall)";
}
