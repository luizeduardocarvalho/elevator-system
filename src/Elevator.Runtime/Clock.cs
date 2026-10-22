using Elevator.Domain;

namespace Elevator.Runtime;

/// <summary>Source of sim time for the shell. The domain never sees a clock; it is handed times explicitly.</summary>
public interface IClock
{
    Instant Now { get; }
}

/// <summary>
/// Sim time driven by the wall clock: one wall second is <c>speed</c> sim seconds. Used by the actor runtime
/// (perf, stress tests, a live system). Monotonic, because it reads a <see cref="System.Diagnostics.Stopwatch"/>.
/// </summary>
public sealed class RealClock : IClock
{
    private readonly Func<TimeSpan> _elapsed;
    private readonly Instant _start;

    /// <param name="speed">Sim seconds per wall second. 0 = frozen at <paramref name="start"/> (the throughput ramp's
    /// steady state, ADR-015): nothing timed ever comes due.</param>
    /// <param name="start">Sim time at creation (a runtime taking over a simulation continues from its time).</param>
    public RealClock(double speed = 1.0, Instant start = default)
        : this(speed, start, ElapsedSince(System.Diagnostics.Stopwatch.StartNew()))
    {
    }

    /// <param name="elapsed">Wall time since creation. Tests pass a fake one to make interleavings deterministic.</param>
    internal RealClock(double speed, Instant start, Func<TimeSpan> elapsed)
    {
        Speed = speed >= 0 && !double.IsNaN(speed) ? speed : throw new ArgumentOutOfRangeException(nameof(speed));
        _start = start;
        _elapsed = elapsed;
    }

    private static Func<TimeSpan> ElapsedSince(System.Diagnostics.Stopwatch stopwatch) => () => stopwatch.Elapsed;

    public double Speed { get; }

    public bool Frozen => Speed == 0;

    public Instant Now => Frozen ? _start : _start.AddMs((long)(_elapsed().TotalMilliseconds * Speed));

    /// <summary>
    /// Wall time until <paramref name="time"/> is reached (zero if it already has; infinite when frozen). Reads
    /// <see cref="Now"/> once: with two reads, a thread preempted in between could compute a negative delay, which
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> rejects.
    /// </summary>
    public TimeSpan WallUntil(Instant time)
    {
        var now = Now;
        return time.Ms <= now.Ms ? TimeSpan.Zero : Frozen ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds((time.Ms - now.Ms) / Speed);
    }

    public TimeSpan WallFor(long simMs) =>
        simMs <= 0 ? TimeSpan.Zero : Frozen ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(simMs / Speed);
}

