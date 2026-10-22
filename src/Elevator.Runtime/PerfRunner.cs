using System.Diagnostics;
using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.EventLog;
using Elevator.Metrics;
using HdrHistogram;

namespace Elevator.Runtime;

/// <summary>Wall-clock latency percentiles of one load level (never sim time, ADR-008).</summary>
public sealed record LatencySummary(double P50Ms, double P95Ms, double P99Ms, double MaxMs);

public sealed record BurstResult(int Submitters, int Submitted, int Assigned, int Lost, int Duplicated, LatencySummary Latency, TimeSpan Wall)
{
    /// <summary>Per actor kind, after the run: where the wall time of the pipeline went.</summary>
    public IReadOnlyList<ActorStats> Stats { get; init; } = [];

    /// <summary>Decisions made by the primary dispatch policy vs the overload fallback.</summary>
    public (int Primary, int Fallback) Decisions { get; init; }

    public double ThroughputPerSecond => Assigned / Math.Max(Wall.TotalSeconds, 1e-9);
}

public sealed record SoakResult(TimeSpan Duration, int Submitted, int Delivered, int Lost, int Duplicated, IReadOnlyList<double> HeapMb, double GrowthPct, LatencySummary Latency);

/// <summary>
/// Load generation against the real actor runtime: many concurrent submitters released together, latency per
/// submission from the call to the accepted assignment (wall clock, Stopwatch), recorded in an HdrHistogram.
/// </summary>
public static class PerfRunner
{
    /// <summary>
    /// <paramref name="submitters"/> async submitters wait on one gate, then each submits
    /// <paramref name="requestsEach"/> requests back to back.
    /// </summary>
    public static async Task<BurstResult> Burst(RuntimeOptions options, int submitters, int requestsEach, TimeSpan timeout, int seed = 1, TextWriter? log = null)
    {
        var checker = new OwnershipChecker();
        var overloaded = false;
        var (primary, fallback) = (0, 0);
        var system = ElevatorSystem.Start(options with { KeepEvents = false }, (seq, e) =>
        {
            checker.Observe(seq, e);
            log?.WriteLine(JsonlEventLog.Serialize(e, seq));
            if (e is OverloadModeChanged o) overloaded = o.Active;
            if (e is AssignmentProposed) { if (overloaded) fallback++; else primary++; }
        });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latencies = new long[submitters * requestsEach];
        var assigned = 0;
        var nextId = 0;

        var tasks = Enumerable.Range(0, submitters).Select(s => Task.Run(async () =>
        {
            var rng = new Random(seed * 100_003 + s);
            await gate.Task.ConfigureAwait(false);
            for (var k = 0; k < requestsEach; k++)
            {
                var id = Interlocked.Increment(ref nextId);
                var a = await system.AssignRequest(RandomRequest(rng, id, options.Floors, system.Clock.Now)).ConfigureAwait(false);
                latencies[id - 1] = a.Latency.StopwatchTicks;
                Interlocked.Increment(ref assigned);
            }
        })).ToArray();

        var started = Stopwatch.GetTimestamp();
        gate.SetResult();
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout)).ConfigureAwait(false);
        var wall = Stopwatch.GetElapsedTime(started);
        if (system.Fault is { } fault) throw new InvalidOperationException("actor fault during perf run", fault);

        await system.DisposeAsync().ConfigureAwait(false);
        var submitted = submitters * requestsEach;
        return new BurstResult(submitters, submitted, assigned, submitted - assigned, checker.Violations,
            Summarize(latencies.Where(t => t > 0)), wall)
        {
            Stats = system.Stats(),
            Decisions = (primary, fallback),
        };
    }

    /// <summary>
    /// Sustained load for <paramref name="duration"/>: submissions at a steady rate the building can serve, with
    /// passengers boarding and riding. Samples the managed heap after a full collection once per second; growth
    /// compares the last quarter of the samples with the first (after warm-up).
    /// </summary>
    public static async Task<SoakResult> Soak(RuntimeOptions options, double requestsPerSecond, TimeSpan duration, TimeSpan warmup, int seed = 1)
    {
        var checker = new OwnershipChecker();
        await using var system = ElevatorSystem.Start(options with { KeepEvents = false }, checker.Observe);
        var rng = new Random(seed);
        var latencies = new System.Collections.Concurrent.ConcurrentBag<long>();
        var pending = new List<Task>();
        var heap = new List<double>();
        var id = 0;
        var interval = TimeSpan.FromSeconds(1 / requestsPerSecond);
        var started = Stopwatch.GetTimestamp();
        var nextSample = warmup;
        var nextSubmit = TimeSpan.Zero;

        while (Stopwatch.GetElapsedTime(started) is var elapsed && elapsed < duration)
        {
            if (elapsed >= nextSubmit)
            {
                var request = RandomRequest(rng, ++id, options.Floors, system.Clock.Now);
                pending.Add(system.AssignRequest(request).ContinueWith(t => { if (t.IsCompletedSuccessfully) latencies.Add(t.Result.Latency.StopwatchTicks); }, TaskScheduler.Default));
                pending.RemoveAll(t => t.IsCompleted);
                nextSubmit += interval;
            }
            if (elapsed >= nextSample)
            {
                heap.Add(GC.GetTotalMemory(forceFullCollection: true) / (1024.0 * 1024.0));
                nextSample += TimeSpan.FromSeconds(1);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds, 50))).ConfigureAwait(false);
        }

        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        if (system.Fault is { } fault) throw new InvalidOperationException("actor fault during soak", fault);
        var quarter = Math.Max(1, heap.Count / 4);
        var first = heap.Take(quarter).Average();
        var last = heap.Skip(heap.Count - quarter).Average();
        return new SoakResult(duration, id, system.PassengersForInspection.Delivered, system.PendingAssignments, checker.Violations,
            heap, 100 * (last - first) / first, Summarize(latencies));
    }

    private static Request RandomRequest(Random rng, int id, int floors, SimTime now)
    {
        var from = rng.Next(1, floors + 1);
        int to;
        do to = rng.Next(1, floors + 1); while (to == from);
        return Request.Create(from, to, now, floors, id: id).Value!;
    }

    private static LatencySummary Summarize(IEnumerable<long> stopwatchTicks)
    {
        var histogram = new LongHistogram(TimeSpan.FromMinutes(5).Ticks, 3);
        var any = false;
        foreach (var t in stopwatchTicks)
        {
            histogram.RecordValue(Math.Max(1, t * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
            any = true;
        }
        if (!any) return new LatencySummary(double.NaN, double.NaN, double.NaN, double.NaN);
        static double Ms(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;
        return new LatencySummary(Ms(histogram.GetValueAtPercentile(50)), Ms(histogram.GetValueAtPercentile(95)),
            Ms(histogram.GetValueAtPercentile(99)), Ms(histogram.GetMaxValue()));
    }

    private sealed class OwnershipChecker
    {
        private readonly Dictionary<int, int> _owner = [];

        public int Violations { get; private set; }

        public void Observe(long seq, DomainEvent e)
        {
            switch (e)
            {
                case AssignmentAccepted a:
                    if (!_owner.TryAdd(a.RequestId, a.ElevatorId)) Violations++;
                    break;
                case AssignmentReleased r:
                    _owner.Remove(r.RequestId);
                    break;
                case PassengerLeftBehind l:
                    _owner.Remove(l.PassengerId);
                    break;
                case PassengerBoarded b:
                    _owner.Remove(b.PassengerId);
                    break;
            }
        }
    }
}
