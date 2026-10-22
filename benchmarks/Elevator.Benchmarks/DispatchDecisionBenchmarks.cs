using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Elevator.Domain;
using Elevator.Policy;
using Elevator.Policy.Dispatch;
using ElevatorUnit = Elevator.Domain.Elevator;

namespace Elevator.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class DispatchDecisionBenchmarks
{
    private const int Floors = 20;
    private DispatchContext _context = null!;
    private readonly LookScheduling _look = new();

    [Params(0, 25, 100, 400, 1600)]
    public int Backlog { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        var id = 0;
        var snapshots = new List<ElevatorSnapshot>();
        for (var e = 0; e < 4; e++)
        {
            var elevator = new ElevatorUnit(e, Floors, ElevatorTiming.Default, _look, 1 + e * 5, capacity: 100_000, bypassThreshold: 1.0);
            for (var k = 0; k < Backlog; k++) elevator.AssignPickup(Random(rng, ++id));
            elevator.Tick(new SimTime(3_000));
            snapshots.Add(elevator.Snapshot());
        }
        _context = new DispatchContext(Random(rng, ++id), snapshots, new SimTime(3_000), ElevatorTiming.Default, 0);
    }

    private static Request Random(Random rng, int id)
    {
        var from = rng.Next(1, Floors + 1);
        int to;
        do to = rng.Next(1, Floors + 1); while (to == from);
        return Request.Create(from, to, SimTime.Zero, Floors, id: id).Value!;
    }

    [Benchmark(Baseline = true)]
    public DispatchChoice? Nearest() => new NearestDispatch().Choose(_context);

    [Benchmark]
    public DispatchChoice? CostBased() => new CostBasedDispatch(_look, ElevatorTiming.Default).Choose(_context);
}
