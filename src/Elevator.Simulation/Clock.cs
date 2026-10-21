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
public sealed class RealClock(double speed = 1.0) : IClock
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    public double Speed { get; } = speed > 0 ? speed : throw new ArgumentOutOfRangeException(nameof(speed));

    public SimTime Now => new((long)(_stopwatch.Elapsed.TotalMilliseconds * Speed));

    /// <summary>Wall time until <paramref name="time"/> is reached (zero if it already has).</summary>
    public TimeSpan WallUntil(SimTime time) => TimeSpan.FromMilliseconds(Math.Max(0, (time.Ms - Now.Ms) / Speed));

    public TimeSpan WallFor(long simMs) => TimeSpan.FromMilliseconds(Math.Max(0, simMs / Speed));
}
