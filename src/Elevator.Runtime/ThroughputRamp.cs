using System.Diagnostics;
using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Simulation;

namespace Elevator.Runtime;

/// <param name="Rates">Offered rates (requests per wall second), in order; the ramp stops after the first saturated step.</param>
/// <param name="BuildingSpeed">0 = the building frozen at the seeded moment (steady state); 1 = real time.</param>
public sealed record RampOptions(
    IReadOnlyList<double> Rates,
    TimeSpan Step,
    TimeSpan Warmup,
    TimeSpan Drain,
    double BuildingSpeed = 0,
    int Seed = 1);

/// <summary>
/// Open-loop, steady-state throughput ramp (ADR-016). Each step starts a fresh actor system from the busiest moment of
/// a scenario (<see cref="PeakMoment"/>), retires every request on acceptance so that backlog stays constant, and
/// offers a fixed rate from one generator thread: request i is due at t0 + i/rate and is submitted then, whether or
/// not earlier ones have been answered. Latency counts from that intended time (<see cref="RampAnalysis"/>).
/// </summary>
public static class ThroughputRamp
{
    /// <summary>First id of generated requests, far above any scenario's passenger ids.</summary>
    public const int FirstId = 10_000_000;

    public static async Task<IReadOnlyList<RampStep>> Run(Scenario scenario, RampOptions options, Action<RampStep>? progress = null)
    {
        var peak = PeakMoment.Find(scenario);
        var steps = new List<RampStep>();
        foreach (var rate in options.Rates)
        {
            var step = await RunStep(scenario, peak.At, rate, options).ConfigureAwait(false);
            steps.Add(step);
            progress?.Invoke(step);
            if (step.Saturated) break;
        }
        return steps;
    }

    public static async Task<RampStep> RunStep(Scenario scenario, SimTime seedAt, double rate, RampOptions options)
    {
        var handover = PeakMoment.HandOverAt(scenario, seedAt);
        var counters = new ProposalCounter();
        var runtime = RuntimeOptions.From(scenario, options.BuildingSpeed) with { KeepEvents = false, RetireOnAccept = true };
        var system = ElevatorSystem.FromEngine(handover, runtime, counters.Observe);

        var f = Stopwatch.Frequency;
        var n = (int)Math.Ceiling(rate * (options.Warmup + options.Step).TotalSeconds);
        var record = new Record(n);
        var period = f / rate;
        var t0 = Stopwatch.GetTimestamp() + f / 50; // 20 ms to start the threads
        var windowStart = t0 + (long)(options.Warmup.TotalSeconds * f);
        var windowEnd = windowStart + (long)(options.Step.TotalSeconds * f);
        (long Proposals, long Fallback, int G0, int G1, int G2) atStart = default, atEnd = default;
        var sampling = new InFlightSampler(system, windowStart, windowEnd);

        var generator = new Thread(() =>
        {
            var rng = new Random(options.Seed);
            var floors = scenario.Floors;
            var windowOpen = false;
            for (var i = 0; i < n; i++)
            {
                var due = t0 + (long)(i * period);
                long now;
                while ((now = Stopwatch.GetTimestamp()) < due) Thread.SpinWait(8);
                if (!windowOpen && due >= windowStart)
                {
                    windowOpen = true;
                    atStart = (counters.Proposals, counters.Fallback, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
                }
                record.Intended[i] = due;
                record.Sent[i] = now;
                var from = rng.Next(1, floors + 1);
                var to = rng.Next(1, floors);
                if (to >= from) to++;
                var request = Request.Create(from, to, system.Clock.Now, floors, id: FirstId + i).Value!;
                var task = system.AssignRequest(request);
                if (task.IsCompleted) record.Complete(i, task);
                else task.ContinueWith(static (t, state) => ((Record.Slot)state!).Complete(t), new Record.Slot(record, i), TaskContinuationOptions.ExecuteSynchronously);
            }
            while (Stopwatch.GetTimestamp() < windowEnd) Thread.SpinWait(64);
            atEnd = (counters.Proposals, counters.Fallback, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        }) { IsBackground = true, Name = "ramp-generator" };

        var sampler = new Thread(sampling.Run) { IsBackground = true, Name = "ramp-in-flight-sampler" };
        sampler.Start();
        generator.Start();
        await Task.Run(generator.Join).ConfigureAwait(false);

        var drainUntil = DateTime.UtcNow + options.Drain;
        while (record.Done < n && DateTime.UtcNow < drainUntil) await Task.Delay(10).ConfigureAwait(false);
        sampling.Stop();
        sampler.Join();
        var fault = system.Fault;
        await system.DisposeAsync().ConfigureAwait(false);
        if (fault is not null) throw new InvalidOperationException("actor fault during the ramp", fault);

        return RampAnalysis.Analyze(new StepSamples(rate, f, windowStart, windowEnd, record.Intended, record.Sent, record.Completed,
            sampling.Average, atEnd.Proposals - atStart.Proposals, atEnd.Fallback - atStart.Fallback,
            (atEnd.G0 - atStart.G0, atEnd.G1 - atStart.G1, atEnd.G2 - atStart.G2)));
    }

    /// <summary>Per-request timestamps; each slot is written by one thread only (the generator, or one continuation).</summary>
    private sealed class Record(int n)
    {
        public long[] Intended { get; } = new long[n];

        public long[] Sent { get; } = new long[n];

        public long[] Completed { get; } = new long[n];

        private int _done;

        public int Done => Volatile.Read(ref _done);

        public void Complete(int i, Task<Assignment> task)
        {
            if (task.IsCompletedSuccessfully) Completed[i] = task.Result.CompletedAt;
            Interlocked.Increment(ref _done);
        }

        public sealed record Slot(Record Owner, int Index)
        {
            public void Complete(Task<Assignment> task) => Owner.Complete(Index, task);
        }
    }

    /// <summary>Counts dispatch proposals, and those made by the overload fallback. Runs on the log writer only.</summary>
    private sealed class ProposalCounter
    {
        private long _proposals;
        private long _fallback;
        private bool _overloaded;

        public long Proposals => Interlocked.Read(ref _proposals);

        public long Fallback => Interlocked.Read(ref _fallback);

        public void Observe(long seq, DomainEvent e)
        {
            if (e is OverloadModeChanged mode) _overloaded = mode.Active;
            else if (e is AssignmentProposed)
            {
                Interlocked.Increment(ref _proposals);
                if (_overloaded) Interlocked.Increment(ref _fallback);
            }
        }
    }

    /// <summary>Samples <see cref="ElevatorSystem.InFlight"/> about every millisecond while inside the window.</summary>
    private sealed class InFlightSampler(ElevatorSystem system, long windowStart, long windowEnd)
    {
        private volatile bool _stop;
        private double _sum;
        private long _count;

        public double Average => _count == 0 ? double.NaN : _sum / _count;

        public void Stop() => _stop = true;

        public void Run()
        {
            while (!_stop)
            {
                var now = Stopwatch.GetTimestamp();
                if (now >= windowEnd) return;
                if (now >= windowStart)
                {
                    _sum += system.InFlight;
                    _count++;
                }
                Thread.Sleep(1);
            }
        }
    }
}
