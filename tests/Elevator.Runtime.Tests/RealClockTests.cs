using Elevator.Domain;

namespace Elevator.Runtime.Tests;

public class RealClockTests
{
    /// <summary>
    /// The race behind a fail-stop seen under CPU load: WallUntil read Now twice, so a thread preempted between the
    /// reads computed (time − a later now) &lt; 0, and CancelAfter threw on the negative delay. The fake elapsed time
    /// jumps 1 s on every read, which is that preemption made deterministic.
    /// </summary>
    [Fact]
    public void Wall_until_reads_the_clock_once_so_it_is_never_negative()
    {
        var elapsed = TimeSpan.Zero;
        var clock = new RealClock(1.0, Instant.Zero, () => (elapsed += TimeSpan.FromSeconds(1)) - TimeSpan.FromSeconds(1));

        var wait = clock.WallUntil(new Instant(500));

        Assert.Equal(TimeSpan.FromMilliseconds(500), wait);
    }

    [Fact]
    public void Wall_until_is_scaled_by_speed_and_zero_once_due()
    {
        var elapsed = TimeSpan.FromMilliseconds(100);
        var clock = new RealClock(10.0, Instant.Zero, () => elapsed);

        Assert.Equal(new Instant(1_000), clock.Now);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.WallUntil(new Instant(2_000)));
        Assert.Equal(TimeSpan.Zero, clock.WallUntil(new Instant(1_000)));
        Assert.Equal(TimeSpan.Zero, clock.WallUntil(new Instant(500)));
    }
}
