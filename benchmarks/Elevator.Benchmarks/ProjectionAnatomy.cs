using Elevator.Domain;
using Elevator.Policy;
using Elevator.Policy.Dispatch;
using ElevatorUnit = Elevator.Domain.Elevator;

namespace Elevator.Benchmarks;

/// <summary>
/// Explains where a cost decision's time goes as the backlog grows, by counting work rather than sampling:
/// policy calls made by the route projections, stops each call scans, and the resulting time per decision.
/// Run: dotnet run -c Release -- anatomy
/// </summary>
public static class ProjectionAnatomy
{
    private sealed class CountingPolicy(ISchedulingPolicy inner) : ISchedulingPolicy
    {
        public long Calls;
        public long StopsScanned;

        public string Name => inner.Name;

        public PolicyDecision Decide(ElevatorSnapshot s)
        {
            Calls++;
            StopsScanned += s.CarCalls.Count + s.HallCalls.Count;
            return inner.Decide(s);
        }
    }

    public static void Run()
    {
        Console.WriteLine("Work for one cost decision (4 elevators, with and without the request): per-passenger oracle vs per-stop projection.");
        Console.WriteLine("Deterministic counts; for timing see the BenchmarkDotNet reports.");
        Console.WriteLine("backlog/elevator | oracle Decide calls | oracle stops scanned | per-stop Decide calls");
        foreach (var backlog in new[] { 0, 25, 100, 400, 1600 })
        {
            var counting = new CountingPolicy(new LookScheduling());
            var rng = new Random(42);
            var id = 0;
            var snapshots = new List<ElevatorSnapshot>();
            for (var e = 0; e < 4; e++)
            {
                var elevator = new ElevatorUnit(e, 20, ElevatorTiming.Default, new LookScheduling(), 1 + e * 5, 100_000, 1.0);
                for (var k = 0; k < backlog; k++) elevator.AssignPickup(Random(rng, ++id));
                elevator.Tick(new SimTime(3_000));
                snapshots.Add(elevator.Snapshot());
            }
            var context = new DispatchContext(Random(rng, ++id), snapshots, new SimTime(3_000), ElevatorTiming.Default, 0);

            // The oracle (per passenger), counted through the policy calls it makes for one decision (with + without, 4 elevators).
            var oracle = new RouteProjection(counting, ElevatorTiming.Default);
            foreach (var s in snapshots) { oracle.Project(s, context.Request, context.Now); oracle.Project(s, null, context.Now); }
            var (calls, scanned) = (counting.Calls, counting.StopsScanned);

            // The per-stop projection: deterministic work units for the same decision.
            var fast = new StopProjection(new LookScheduling(), ElevatorTiming.Default);
            var steps = snapshots.Sum(s => fast.Project(s, context.Request, context.Now).Steps + fast.Project(s, null, context.Now).Steps);

            Console.WriteLine($"{backlog,16} | {calls,19} | {scanned,20} | {steps,21}");
        }
    }

    private static Request Random(Random rng, int id)
    {
        var from = rng.Next(1, 21);
        int to;
        do to = rng.Next(1, 21); while (to == from);
        return Request.Create(from, to, SimTime.Zero, 20, id: id).Value!;
    }
}
