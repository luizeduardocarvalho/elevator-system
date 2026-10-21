namespace Elevator.Runtime.Tests;

/// <summary>
/// Perf as a test gate (real clock, real threads), against the spec: assignment within 100 ms for 100+ concurrent
/// requests, nothing lost or duplicated, memory bounded. Category "Perf" so CI can run it on a quiet machine.
/// The full curve (100 to 10k) is measured by `elevator perf` (docs/results.md).
/// </summary>
[Trait("Category", "Perf")]
[Collection(nameof(PerfCollection))]
public class PerfGateTests
{
    private static readonly RuntimeOptions Default = new(Floors: 20, Elevators: 4, Capacity: 10, Speed: 1); // look+cost, next-trip, fallback 200

    [Fact]
    public async Task Thousand_concurrent_submitters_are_assigned_within_100ms_p99_with_nothing_lost_or_duplicated()
    {
        await PerfRunner.Burst(Default, 200, 5, TimeSpan.FromSeconds(60), seed: 99); // warm-up (JIT, thread pool)

        var result = await PerfRunner.Burst(Default, submitters: 1_000, requestsEach: 1, TimeSpan.FromSeconds(60));

        Assert.Equal((1_000, 0, 0), (result.Assigned, result.Lost, result.Duplicated));
        Assert.True(result.Latency.P99Ms < 100, $"p99 {result.Latency.P99Ms:0.0} ms ≥ 100 ms");
    }

    [Fact]
    public async Task Sustained_load_keeps_the_heap_flat()
    {
        var soak = await PerfRunner.Soak(Default with { Speed = 100 }, requestsPerSecond: 20, TimeSpan.FromSeconds(20), warmup: TimeSpan.FromSeconds(5));

        Assert.Equal((0, 0), (soak.Lost, soak.Duplicated));
        Assert.True(soak.GrowthPct < 10, $"heap grew {soak.GrowthPct:0.0}% ({string.Join(", ", soak.HeapMb.Select(m => m.ToString("0.00")))} MB)");
    }
}

/// <summary>Wall-clock measurements must not share the CPU with other tests: this collection runs alone.</summary>
[CollectionDefinition(nameof(PerfCollection), DisableParallelization = true)]
public sealed class PerfCollection;
