using Elevator.Tui.Loop;

namespace Elevator.Tui.Tests;

public class PacerTests
{
    private static int[] Frames(Pacer p, int count, double realMs = 50) => [.. Enumerable.Range(0, count).Select(_ => p.TicksDue(realMs))];

    [Fact]
    public void Half_speed_at_20_fps_runs_one_tick_every_fourth_frame()
    {
        // 50 ms × 0.5 = 25 sim ms per frame; a tick is 100 ms.
        Assert.Equal([0, 0, 0, 1, 0, 0, 0, 1], Frames(new Pacer(0.5), 8));
    }

    [Theory]
    [InlineData(0.25, 1)]  // 12.5 ms per frame: 1 tick in 8 frames
    [InlineData(1.0, 4)]
    [InlineData(2.0, 8)]
    [InlineData(10.0, 40)]
    [InlineData(50.0, 200)]
    public void Ticks_over_eight_frames_match_the_speed(double speed, int ticks) => Assert.Equal(ticks, Frames(new Pacer(speed), 8).Sum());

    [Fact]
    public void The_remainder_carries_between_frames()
    {
        var p = new Pacer(1);
        Assert.Equal(0, p.TicksDue(70));
        Assert.Equal(70, p.AccumulatorMs, 6);
        Assert.Equal(1, p.TicksDue(70));
        Assert.Equal(40, p.AccumulatorMs, 6);
    }

    [Fact]
    public void A_huge_frame_is_capped_and_the_excess_is_dropped_not_carried()
    {
        var p = new Pacer(50);
        Assert.Equal(Pacer.MaxTicksPerFrame, p.TicksDue(10 * 60_000)); // a 10-minute stall at 50× owes 300,000 ticks
        Assert.Equal(0, p.AccumulatorMs);
        Assert.Equal(25, p.TicksDue(50)); // next frame is normal again
    }

    [Fact]
    public void Budget_exceeded_drops_what_is_owed()
    {
        var p = new Pacer(10);
        p.TicksDue(73);
        p.BudgetExceeded();
        Assert.Equal(0, p.AccumulatorMs);
    }

    [Fact]
    public void Max_asks_for_the_cap_and_lets_the_budget_decide()
    {
        var p = new Pacer(null);
        Assert.Equal(("MAX", Pacer.MaxTicksPerFrame), (p.SpeedLabel, p.TicksDue(50)));
    }

    [Fact]
    public void Pause_runs_nothing_and_single_step_runs_exactly_one_tick()
    {
        var p = new Pacer(10);
        p.TogglePause();
        Assert.Equal([0, 0], Frames(p, 2));
        p.StepOnce();
        p.StepOnce();
        Assert.Equal([2, 0], Frames(p, 2));
        p.TogglePause();
        Assert.Equal(5, p.TicksDue(50));
    }

    [Fact]
    public void Step_while_running_pauses_first()
    {
        var p = new Pacer(10);
        p.StepOnce();
        Assert.True(p.Paused);
        Assert.Equal(1, p.TicksDue(50));
    }

    [Fact]
    public void Speed_ladder_clamps_at_both_ends()
    {
        var p = new Pacer(0.25);
        p.Slower();
        Assert.Equal("0.25×", p.SpeedLabel);
        foreach (var expected in new[] { "0.5×", "1×", "2×", "5×", "10×", "50×", "MAX", "MAX" })
        {
            p.Faster();
            Assert.Equal(expected, p.SpeedLabel);
        }
    }

    [Theory]
    [InlineData("10", 10.0)]
    [InlineData("0.5x", 0.5)]
    [InlineData("MAX", null)]
    public void Speeds_parse(string text, double? expected) => Assert.Equal(expected, Pacer.ParseSpeed(text));

    [Fact]
    public void Unknown_speed_is_rejected() => Assert.Throws<FormatException>(() => Pacer.ParseSpeed("3"));

    [Fact]
    public void Achieved_speed_is_sim_over_real_across_the_last_second()
    {
        var m = new SpeedMeter();
        Assert.Null(m.Achieved);
        for (var frame = 0; frame <= 40; frame++) m.Record(TimeSpan.FromMilliseconds(frame * 50), frame * 500); // 10×
        Assert.Equal(10.0, m.Achieved!.Value, 6);
        for (var frame = 41; frame <= 80; frame++) m.Record(TimeSpan.FromMilliseconds(frame * 50), 20_000 + (frame - 40) * 100); // now 2×
        Assert.Equal(2.0, m.Achieved!.Value, 6); // the window forgot the 10× second
    }
}
