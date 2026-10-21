using System.Globalization;

namespace Elevator.Tui.Loop;

/// <summary>
/// Fixed sim tick plus an accumulator (ADR-011), the classic "fix your timestep" loop:
/// <list type="bullet">
/// <item>The sim only ever advances in whole ticks of <see cref="TickMs"/> sim ms, so the engine sees the same
/// calls whatever the frame rate (and its log is step-independent anyway, shared v3 contract).</item>
/// <item>Each frame adds <c>realDelta × speed</c> to the accumulator and runs as many whole ticks as it holds;
/// the remainder carries to the next frame, so 0.5× at 20 fps is exactly one tick every fourth frame.</item>
/// <item>At most <see cref="MaxTicksPerFrame"/> ticks per frame (and the loop's work budget). Anything beyond is
/// <b>dropped, not carried</b>: a slow frame (a debugger pause, a laptop waking up) must not trigger an
/// ever-growing catch-up ("spiral of death"). The achieved speed then reads below the target, which the status
/// bar shows.</item>
/// <item>MAX means "as many ticks as fit in the frame's work budget".</item>
/// </list>
/// Pure and clock-free: the loop passes in the real time elapsed, so tests drive it with exact numbers.
/// </summary>
public sealed class Pacer
{
    public const long TickMs = 100;
    public const int MaxTicksPerFrame = 2_000;

    /// <summary>The speed ladder for + and -; null = MAX.</summary>
    public static IReadOnlyList<double?> Speeds { get; } = [0.25, 0.5, 1, 2, 5, 10, 50, null];

    private double _accumulatorMs;
    private int _pendingSteps;

    public Pacer(double? speed = 10)
    {
        var index = Speeds.ToList().IndexOf(speed);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(speed), $"speed must be one of {string.Join(", ", Speeds.Select(Label))}");
        SpeedIndex = index;
    }

    public int SpeedIndex { get; private set; }

    /// <summary>Sim seconds per real second; null = MAX.</summary>
    public double? Speed => Speeds[SpeedIndex];

    public bool Paused { get; private set; }

    /// <summary>Sim ms owed but not yet run (always below one tick after a frame, unless paused).</summary>
    public double AccumulatorMs => _accumulatorMs;

    public string SpeedLabel => Label(Speed);

    public static string Label(double? speed) => speed is { } s ? s.ToString("0.##", CultureInfo.InvariantCulture) + "×" : "MAX";

    /// <summary>Parses "0.25", "10", "10x", "max".</summary>
    public static double? ParseSpeed(string text)
    {
        if (text.Trim().Equals("max", StringComparison.OrdinalIgnoreCase)) return null;
        var t = text.Trim().TrimEnd('x', 'X', '×');
        var value = double.Parse(t, CultureInfo.InvariantCulture);
        if (!Speeds.Contains(value)) throw new FormatException($"speed must be one of {string.Join(", ", Speeds.Select(Label))}");
        return value;
    }

    public void Faster() => SetSpeedIndex(SpeedIndex + 1);

    public void Slower() => SetSpeedIndex(SpeedIndex - 1);

    private void SetSpeedIndex(int index)
    {
        SpeedIndex = Math.Clamp(index, 0, Speeds.Count - 1);
        _accumulatorMs = Math.Min(_accumulatorMs, TickMs - 1); // never a burst of owed ticks at the new speed
    }

    public void TogglePause()
    {
        Paused = !Paused;
        _accumulatorMs = 0; // resuming starts a fresh accumulation, not a catch-up of the pause
    }

    /// <summary>Single-step: pauses (if running) and owes exactly one tick to the next frame.</summary>
    public void StepOnce()
    {
        if (!Paused) TogglePause();
        _pendingSteps++;
    }

    /// <summary>Whole ticks to run this frame, given the real time since the previous frame.</summary>
    public int TicksDue(double realDeltaMs)
    {
        if (Paused)
        {
            var steps = _pendingSteps;
            _pendingSteps = 0;
            return steps;
        }
        if (Speed is not { } speed) return MaxTicksPerFrame;

        _accumulatorMs += Math.Max(0, realDeltaMs) * speed;
        var ticks = (long)(_accumulatorMs / TickMs);
        if (ticks > MaxTicksPerFrame)
        {
            _accumulatorMs = 0; // the excess is dropped, not carried
            return MaxTicksPerFrame;
        }
        _accumulatorMs -= ticks * TickMs;
        return (int)ticks;
    }

    /// <summary>The frame ran out of work budget before running every due tick: drop what is owed.</summary>
    public void BudgetExceeded() => _accumulatorMs = 0;
}

/// <summary>
/// Achieved speed = sim time advanced / real time elapsed over the last second of frames. Shown next to the
/// target so a sim that cannot keep up (or a capped frame) is visible, never silent.
/// </summary>
public sealed class SpeedMeter(double windowMs = 1_000)
{
    private readonly Queue<(double RealMs, long SimMs)> _samples = new();

    public void Record(TimeSpan real, long simMs)
    {
        var realMs = real.TotalMilliseconds;
        _samples.Enqueue((realMs, simMs));
        while (_samples.Count > 2 && realMs - _samples.Peek().RealMs > windowMs) _samples.Dequeue();
    }

    /// <summary>Null until two samples span some real time.</summary>
    public double? Achieved
    {
        get
        {
            if (_samples.Count < 2) return null;
            var first = _samples.Peek();
            var last = _samples.Last();
            var real = last.RealMs - first.RealMs;
            return real <= 0 ? null : (last.SimMs - first.SimMs) / real;
        }
    }

    /// <summary>After a seek the sim jumped: start measuring afresh.</summary>
    public void Reset() => _samples.Clear();
}
