using Elevator.Domain;
using Elevator.Runtime;

namespace Elevator.Simulation;

/// <summary>A clock that only moves when told to. Tests and headless runs use it; no sleeping, ever.</summary>
public sealed class SimClock : IClock
{
    public Instant Now { get; private set; } = Instant.Zero;

    public void AdvanceTo(Instant time)
    {
        if (time < Now) throw new ArgumentOutOfRangeException(nameof(time), $"time went backwards: {time} < {Now}");
        Now = time;
    }

    public void Advance(long ms) => AdvanceTo(Now.AddMs(ms));
}
