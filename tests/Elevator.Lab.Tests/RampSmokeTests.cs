using Elevator.Simulation;

namespace Elevator.Lab.Tests;

/// <summary>
/// A short real ramp (ADR-016): two 2-second steps from the lunch peak, frozen building, retire on accept. Nothing is
/// lost, the generator keeps its schedule, and the in-flight count agrees with Little's law. The full ramp is run by
/// `elevator perf --ramp` (docs/results/perf-ramp.md).
/// </summary>
[Trait("Category", "Perf")]
[Collection(nameof(PerfCollection))]
public class RampSmokeTests
{
    [Fact]
    public async Task A_short_ramp_keeps_up_loses_nothing_and_agrees_with_littles_law()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "ElevatorSystem.slnx"))) root = root.Parent!;
        var lunch = ScenarioLoader.LoadFile(Path.Combine(root.FullName, "scenarios", "lunch.yaml")).Scenario!;
        var options = new RampOptions([1_000, 3_000], Step: TimeSpan.FromSeconds(2), Warmup: TimeSpan.FromSeconds(0.5), Drain: TimeSpan.FromSeconds(5));

        var steps = await ThroughputRamp.Run(lunch, options);

        Assert.Equal(2, steps.Count);
        Assert.All(steps, s =>
        {
            Assert.Equal(0, s.Unfinished);
            Assert.False(s.Saturated, $"{s.Offered}/s: achieved {s.Achieved:0}");
            Assert.True(s.Valid, $"{s.Offered}/s: generator lag p99 {s.LagP99Ms:0.000} ms");
            Assert.True(s.P99Ms < 100, $"{s.Offered}/s: p99 {s.P99Ms:0.0} ms");
            Assert.True(s.LittleDeviationPct < 25, $"{s.Offered}/s: sampled {s.InFlightSampled:0.00} vs λW {s.LittleL:0.00}");
        });
    }
}
