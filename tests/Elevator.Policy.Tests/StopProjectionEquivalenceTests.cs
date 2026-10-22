using Elevator.Domain;
using Elevator.Domain.Events;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ElevatorUnit = Elevator.Domain.Elevator;

namespace Elevator.Policy.Tests;

/// <summary>
/// Differential test: the per-stop <see cref="StopProjection"/> must give exactly the same pickup time, load at pickup
/// and added delay as the per-passenger <see cref="RouteProjection"/> (the oracle, which runs the real elevator), on
/// random elevators of random types (timing, served floors). That is what makes the dispatcher's decisions, and so the
/// bench, byte-identical.
/// </summary>
public class StopProjectionEquivalenceTests
{
    /// <param name="Timing">The elevator type's timing (ADR-012); the projections are built with the default and must
    /// take the snapshot's own.</param>
    /// <param name="Served">The floors the elevator's type serves; every trip starts and ends on one of them.</param>
    public sealed record Case(int Capacity, double Threshold, int Start, (int From, int To, long AtMs)[] Assigned,
        int Boarded, long SnapshotAtMs, long NowDelayMs, (int From, int To) Extra, ElevatorTiming Timing, int[] Served,
        int[] Loads, int ExtraLoad);

    private const int Floors = 12;

    // Elevator types: every floor; an express (lobby + top half); odd floors only; a low-rise local.
    private static readonly int[][] ServedSets =
    [
        [.. Enumerable.Range(1, Floors)],
        [1, 7, 8, 9, 10, 11, 12],
        [1, 3, 5, 7, 9, 11],
        [1, 2, 3, 4, 5, 6],
    ];

    private static readonly ElevatorTiming[] Timings =
    [
        ElevatorTiming.Default,
        new(FloorTravelMs: 1_000, DoorOpeningMs: 800, DoorDwellMs: 2_000, DoorClosingMs: 800),  // express
        new(FloorTravelMs: 3_000, DoorOpeningMs: 2_000, DoorDwellMs: 6_000, DoorClosingMs: 2_500), // freight
    ];

    private static Gen<(int, int)> TripIn(int[] floors) =>
        from i in Gen.Choose(0, floors.Length - 1)
        from o in Gen.Choose(1, floors.Length - 1)
        select (floors[i], floors[(i + o) % floors.Length]);

    private static Gen<Case> Cases =>
        from capacity in Gen.Elements(1, 2, 4, 10, 1000)
        from threshold in Gen.Elements(0.5, 0.8, 1.0)
        from served in Gen.Elements(ServedSets)
        from timing in Gen.Elements(Timings)
        from start in Gen.Elements(served)
        from n in Gen.Choose(0, 25)
        from assigned in Gen.ArrayOf(from t in TripIn(served) from at in Gen.Choose(0, 40_000) select (t.Item1, t.Item2, (long)at), n)
        from boarded in Gen.Choose(0, 6)
        from at in Gen.Choose(0, 90_000)
        from delay in Gen.Elements(0L, 0L, 1_500L, 9_000L)
        from extra in TripIn(served)
        from heavy in Gen.Elements(false, false, true) // loads above one unit (freight, ADR-012)
        from loads in Gen.ArrayOf(heavy ? Gen.Elements(1, 1, 2, 3, 6) : Gen.Constant(1), n)
        from extraLoad in heavy ? Gen.Elements(1, 2, 4) : Gen.Constant(1)
        select new Case(capacity, threshold, start, assigned, boarded, at, delay, extra, timing, served, loads, extraLoad);

    [Property(MaxTest = 1500)]
    public Property Per_stop_projection_equals_the_per_passenger_oracle() => Prop.ForAll(Cases.ToArbitrary(), c =>
    {
        var policy = new LookScheduling();
        var snapshot = BuildSnapshot(c, policy);
        var now = snapshot.At.AddMs(c.NowDelayMs);
        var extra = Request.Create(c.Extra.From, c.Extra.To, now, Floors, id: 99_999, load: c.ExtraLoad).Value!;

        var oracle = new RouteProjection(policy, ElevatorTiming.Default, StopProjection.DefaultHorizonMs); // same horizon
        var fast = new StopProjection(policy, ElevatorTiming.Default);

        var oWith = oracle.Project(snapshot, extra, now);
        var oWithout = oracle.Project(snapshot, null, now);
        var fWith = fast.Project(snapshot, extra, now);
        var fWithout = fast.Project(snapshot, null, now);

        Assert.Equal(oWith.PickupAt, fWith.PickupAt);
        Assert.Equal(oWith.LoadAtPickup, fWith.LoadAtPickup);
        long oracleDelay = 0;
        foreach (var (id, done) in oWithout.Completions)
            oracleDelay += (oWith.Completions.TryGetValue(id, out var x) ? x : oWith.Horizon).Ms - done.Ms;
        var fastDelay = StopProjection.AddedDelayMs(fWithout, fWith);
        if (oracleDelay != fastDelay && Environment.GetEnvironmentVariable("PROJ_DEBUG") is not null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"snapshot: floor {snapshot.CurrentFloor} {snapshot.State} dir {snapshot.CommittedDirection} at {snapshot.At} phase {snapshot.PhaseEndsAt} load {snapshot.Load} cap {snapshot.Capacity}x{snapshot.BypassThreshold} now {now}");
            sb.AppendLine("car calls: " + string.Join(",", snapshot.CarCalls));
            sb.AppendLine("riders: " + string.Join(",", snapshot.Riders.Select(r => $"{r.Id}:{r.PickupFloor}->{r.DestinationFloor}")));
            foreach (var h in snapshot.HallCalls) sb.AppendLine($"pickup {h.Floor}{h.Direction}: " + string.Join(",", h.Requests.Select(r => $"{r.Id}:->{r.DestinationFloor}")));
            sb.AppendLine($"extra {extra.PickupFloor}->{extra.DestinationFloor}; oracle pickup {oWith.PickupAt} fast {fWith.PickupAt}");
            sb.AppendLine("oracle without: " + string.Join(" ", oWithout.Completions.OrderBy(k => k.Key).Select(k => $"{k.Key}@{k.Value.Ms}")));
            sb.AppendLine("oracle with:    " + string.Join(" ", oWith.Completions.OrderBy(k => k.Key).Select(k => $"{k.Key}@{k.Value.Ms}")));
            sb.AppendLine("fast without: " + fWithout.Describe());
            sb.AppendLine("fast with:    " + fWith.Describe());
            File.WriteAllText("/tmp/claude-501/proj-debug.txt", sb.ToString());
        }
        Assert.Equal(oracleDelay, fastDelay);
    });

    /// <summary>A real elevator driven to a random state: assignments over time, some riders boarding, mid-route.</summary>
    private static ElevatorSnapshot BuildSnapshot(Case c, ISchedulingPolicy policy)
    {
        var e = new ElevatorUnit(0, Floors, c.Timing, policy, c.Start, c.Capacity, c.Threshold, FloorMask.Of(c.Served), "typed");
        var id = 0;
        var boardedSoFar = 0;
        foreach (var (from, to, at, load) in c.Assigned.Zip(c.Loads, (a, l) => (a.From, a.To, a.AtMs, l)).OrderBy(a => a.AtMs))
        {
            if (at > e.Now.Ms) Advance(e, at, c.Boarded, ref boardedSoFar);
            if (e.State == ElevatorState.Maintenance) continue;
            e.AssignPickup(Request.Create(from, to, new SimTime(at), Floors, id: ++id, load: load).Value!);
            e.DrainEvents();
        }
        Advance(e, Math.Max(e.Now.Ms, c.SnapshotAtMs), c.Boarded, ref boardedSoFar);
        return e.Snapshot();
    }

    /// <summary>Moves time forward, letting up to <paramref name="maxBoard"/> waiting requests board when answered.</summary>
    private static void Advance(ElevatorUnit e, long until, int maxBoard, ref int boarded)
    {
        var waiting = e.HallCalls.ToDictionary(h => (h.Floor, h.Direction), h => h.Requests.ToList());
        while (e.NextDeadline is { } d && d.Ms <= until)
        {
            e.Tick(d);
            foreach (var ev in e.DrainEvents())
            {
                if (ev is DoorsOpened opened)
                    foreach (var r in e.Riders.Where(r => r.DestinationFloor == opened.Floor).ToList()) e.Alight(r.Id);
                if (ev is HallCallAnswered a && waiting.Remove((a.Floor, a.Direction), out var list))
                    foreach (var r in list)
                    {
                        if (boarded >= maxBoard || e.IsFull) break;
                        if (e.Load + r.Load > e.Capacity) break; // does not fit: left behind with everyone behind, as the passengers do
                        e.Board(r);
                        boarded++;
                    }
            }
            e.DrainEvents();
            waiting = e.HallCalls.ToDictionary(h => (h.Floor, h.Direction), h => h.Requests.ToList());
        }
        e.Tick(new SimTime(Math.Max(until, e.Now.Ms)));
        e.DrainEvents();
    }
}
