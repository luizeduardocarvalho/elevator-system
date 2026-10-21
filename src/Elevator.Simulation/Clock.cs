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
