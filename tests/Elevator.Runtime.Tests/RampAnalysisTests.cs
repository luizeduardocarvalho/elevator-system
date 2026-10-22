namespace Elevator.Runtime.Tests;

/// <summary>
/// The ramp's step analysis (ADR-016), on synthetic timestamps. Frequency 1,000,000 ticks/s, so 1 tick = 1 µs.
/// </summary>
public class RampAnalysisTests
{
    private const long F = 1_000_000;

    /// <summary>1,000 req/s for 2 s: the first second is warm-up, the second is the window. Every request is sent on
    /// time (plus <paramref name="lagUs"/>) and accepted <paramref name="latencyUs"/> after it was sent.</summary>
    private static StepSamples Steady(long latencyUs, long lagUs = 0, int? completeOnly = null, double inFlight = double.NaN)
    {
        const int n = 2_000;
        var intended = new long[n];
        var sent = new long[n];
        var completed = new long[n];
        for (var i = 0; i < n; i++)
        {
            intended[i] = i * 1_000L;
            sent[i] = intended[i] + lagUs;
            completed[i] = completeOnly is { } k && i >= k ? 0 : sent[i] + latencyUs;
        }
        return new StepSamples(OfferedPerSecond: 1_000, Frequency: F, WindowStart: F, WindowEnd: 2 * F, Intended: intended, Sent: sent,
            Completed: completed, InFlightAverage: double.IsNaN(inFlight) ? 1_000 * latencyUs / 1e6 : inFlight,
            Proposals: 1_000, FallbackProposals: 250, Gc: (3, 1, 0));
    }

    [Fact]
    public void A_steady_step_reports_offered_achieved_latency_and_littles_law()
    {
        var step = RampAnalysis.Analyze(Steady(latencyUs: 2_000));

        Assert.Equal(1_000, step.Offered);
        Assert.Equal(1_000, step.Achieved, 6); // completions inside the window, per second
        Assert.Equal(1_000, step.Measured);    // intended inside the window
        Assert.Equal(0, step.Unfinished);
        Assert.Equal((2.0, 2.0, 2.0, 2.0), (step.P50Ms, step.P99Ms, step.P999Ms, step.MaxMs));
        Assert.Equal(2.0, step.LittleL, 6);     // λ·W = 1000/s · 2 ms
        Assert.Equal(0, step.LittleDeviationPct, 6);
        Assert.Equal(25.0, step.FallbackPct, 6);
        Assert.Equal((3, 1, 0), step.Gc);
        Assert.True(step.Valid);
        Assert.False(step.Saturated);
    }

    [Fact]
    public void Latency_is_measured_from_the_intended_send_time_not_the_actual_one()
    {
        var step = RampAnalysis.Analyze(Steady(latencyUs: 2_000, lagUs: 3_000));
        Assert.Equal(5.0, step.P99Ms, 6);    // 3 ms late + 2 ms in the system
        Assert.Equal(3.0, step.LagP99Ms, 6);
        Assert.False(step.Valid);            // the generator itself was more than 1 ms late
    }

    [Fact]
    public void Unfinished_requests_count_and_mark_the_step_saturated()
    {
        var step = RampAnalysis.Analyze(Steady(latencyUs: 2_000, completeOnly: 1_800));
        Assert.Equal(200, step.Unfinished);
        Assert.Equal(802, step.Achieved, 6); // completions inside the window: requests 998..1799
        Assert.True(step.Saturated); // 802 < 99 % of 1,000
        Assert.True(step.MaxMs >= 2.0);
    }

    [Fact]
    public void A_sampled_in_flight_count_far_from_little_is_flagged()
    {
        var step = RampAnalysis.Analyze(Steady(latencyUs: 2_000, inFlight: 3.0));
        Assert.Equal(50.0, step.LittleDeviationPct, 6); // |3 − 2| / 2
        Assert.False(step.LittleHolds);
        Assert.True(RampAnalysis.Analyze(Steady(latencyUs: 2_000, inFlight: 2.05)).LittleHolds);
    }

    [Fact]
    public void The_ramp_stops_after_the_first_saturated_step()
    {
        Assert.False(RampAnalysis.Analyze(Steady(2_000)).Saturated);
        Assert.True(RampAnalysis.Analyze(Steady(2_000, completeOnly: 1_985)).Saturated);  // 987 < 990
        Assert.False(RampAnalysis.Analyze(Steady(2_000, completeOnly: 1_995)).Saturated); // 997 ≥ 990
    }
}
