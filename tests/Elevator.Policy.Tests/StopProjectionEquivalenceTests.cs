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
/// random elevators. That is what makes the dispatcher's decisions, and so the bench, byte-identical.
/// </summary>
public class StopProjectionEquivalenceTests
{
    public sealed record Case(string Policy, int Capacity, double Threshold, int Start, (int From, int To, long AtMs)[] Assigned,
        int Boarded, long SnapshotAtMs, long NowDelayMs, (int From, int To) Extra, CapacityMode Mode);

    private const int Floors = 12;

    private static Gen<(int, int)> Trip =>
        from f in Gen.Choose(1, Floors)
        from o in Gen.Choose(1, Floors - 1)
        select (f, (f - 1 + o) % Floors + 1);

    private static Gen<Case> Cases =>
        from policy in Gen.Elements("look", "fifo")
        from capacity in Gen.Elements(1, 2, 4, 10, 1000)
        from threshold in Gen.Elements(0.5, 0.8, 1.0)
        from start in Gen.Choose(1, Floors)
        from n in Gen.Choose(0, 25)
        from assigned in Gen.ArrayOf(from t in Trip from at in Gen.Choose(0, 40_000) select (t.Item1, t.Item2, (long)at), n)
        from boarded in Gen.Choose(0, 6)
        from at in Gen.Choose(0, 90_000)
        from delay in Gen.Elements(0L, 0L, 1_500L, 9_000L)
        from extra in Trip
        from mode in Gen.Elements(CapacityMode.NextTrip, CapacityMode.Defer)
        select new Case(policy, capacity, threshold, start, assigned, boarded, at, delay, extra, mode);

    [Property(MaxTest = 1500)]
    public Property Per_stop_projection_equals_the_per_passenger_oracle() => Prop.ForAll(Cases.ToArbitrary(), c =>
    {
        var policy = SchedulingPolicies.Create(c.Policy);
        var snapshot = BuildSnapshot(c, policy);
        var now = snapshot.At.AddMs(c.NowDelayMs);
        var extra = Request.Create(c.Extra.From, c.Extra.To, now, Floors, id: 99_999).Value!;

        var oracle = new RouteProjection(policy, ElevatorTiming.Default);
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
        var e = new ElevatorUnit(0, Floors, ElevatorTiming.Default, policy, c.Start, c.Capacity, c.Threshold, c.Mode);
        var id = 0;
        var boardedSoFar = 0;
        foreach (var (from, to, at) in c.Assigned.OrderBy(a => a.AtMs))
        {
            if (at > e.Now.Ms) Advance(e, at, c.Boarded, ref boardedSoFar);
            if ((c.Mode == CapacityMode.Defer && e.IsFull) || e.State == ElevatorState.Maintenance) continue;
            e.AssignPickup(Request.Create(from, to, new SimTime(at), Floors, id: ++id).Value!);
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
