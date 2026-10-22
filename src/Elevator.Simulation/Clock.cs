using Elevator.Domain;

namespace Elevator.Simulation;

/// <summary>Source of sim time for the shell. The domain never sees a clock; it is handed times explicitly.</summary>
public interface IClock
{
    SimTime Now { get; }
}

/// <summary>A clock that only moves when told to. Tests and headless runs use it; no sleeping, ever.</summary>
public sealed class SimClock : IClock
{
    public SimTime Now { get; private set; } = SimTime.Zero;

    public void AdvanceTo(SimTime time)
    {
        if (time < Now) throw new ArgumentOutOfRangeException(nameof(time), $"time went backwards: {time} < {Now}");
        Now = time;
    }

    public void Advance(long ms) => AdvanceTo(Now.AddMs(ms));
}

/// <summary>
/// Sim time driven by the wall clock: one wall second is <c>speed</c> sim seconds. Used by the actor runtime
/// (perf, stress tests, a live system). Monotonic, because it reads a <see cref="System.Diagnostics.Stopwatch"/>.
/// </summary>
public sealed class RealClock : IClock
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private readonly SimTime _start;

    /// <param name="speed">Sim seconds per wall second. 0 = frozen at <paramref name="start"/> (the throughput ramp's
    /// steady state, ADR-015): nothing timed ever comes due.</param>
    /// <param name="start">Sim time at creation (a runtime taking over a simulation continues from its time).</param>
    public RealClock(double speed = 1.0, SimTime start = default)
    {
        Speed = speed >= 0 && !double.IsNaN(speed) ? speed : throw new ArgumentOutOfRangeException(nameof(speed));
        _start = start;
    }

    public double Speed { get; }

    public bool Frozen => Speed == 0;

    public SimTime Now => Frozen ? _start : _start.AddMs((long)(_stopwatch.Elapsed.TotalMilliseconds * Speed));

    /// <summary>Wall time until <paramref name="time"/> is reached (zero if it already has; infinite when frozen).</summary>
    public TimeSpan WallUntil(SimTime time) =>
        time.Ms <= Now.Ms ? TimeSpan.Zero : Frozen ? System.Threading.Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds((time.Ms - Now.Ms) / Speed);

    public TimeSpan WallFor(long simMs) =>
        simMs <= 0 ? TimeSpan.Zero : Frozen ? System.Threading.Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(simMs / Speed);
}

