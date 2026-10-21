using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Metrics;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Simulation.Tests;

/// <summary>
/// Random buildings, traffic, capacities, latencies and strategies through the full pipeline, checking the
/// system-level invariants from the event log.
/// </summary>
public class MultiElevatorProperties
{
    public sealed record Case(Scenario Scenario, Strategy Strategy);

    public static readonly string[] Schedulings = ["fifo", "look"];
    public static readonly string[] Dispatches = ["roundrobin", "nearest", "cost"];

    private static Gen<Case> CaseGen() =>
        from floors in Gen.Choose(3, 20)
        from elevators in Gen.Choose(1, 5)
        from capacity in Gen.Choose(1, 8)
        from threshold in Gen.Elements(0.5, 0.8, 1.0)
        from snapshotMs in Gen.Elements(0L, 0L, 1_500L, 6_000L)
        from dispatchMs in Gen.Elements(0L, 0L, 500L)
        from count in Gen.Choose(1, 40)
        from specs in Gen.ArrayOf(PassengerGen(floors), count)
        from scheduling in Gen.Elements(Schedulings)
        from dispatch in Gen.Elements(Dispatches)
        from rebalance in Gen.Elements(false, true)
        from mode in Gen.Elements(CapacityMode.NextTrip, CapacityMode.Defer)
        select new Case(
            new Scenario("prop", 1, floors, ElevatorTiming.Default, scheduling, SimTime.FromSeconds(200_000), specs, null,
                elevators, capacity, threshold, dispatch, null, new MessageLatency(snapshotMs, dispatchMs), mode),
            new Strategy(scheduling, dispatch, rebalance));

    private static Gen<PassengerSpec> PassengerGen(int floors) =>
        from at in Gen.Choose(0, 240_000)
        from from_ in Gen.Choose(1, floors)
        from offset in Gen.Choose(1, floors - 1)
        select new PassengerSpec(new SimTime(at), from_, (from_ - 1 + offset) % floors + 1);

    /// <summary>Regression: a rebalance hands passenger 4 to E1 as its doors open at floor 2, so 4 boards right after
    /// the later caller 8 in the same batch (look+roundrobin+rebalance, 3 elevators, 17 floors).</summary>
    [Property(Replay = "12939244975951197390,4127911998706265127,76", MaxTest = 1)]
    public Property Same_batch_boarding_after_a_rebalance_is_not_a_skip() => System_invariants_hold();

    [Property(MaxTest = 120)]
    public Property System_invariants_hold() => Prop.ForAll(CaseGen().ToArbitrary(), c =>
    {
        var result = SimEngine.Run(c.Scenario, c.Strategy);
        var passengers = ScenarioRunner.BuildPassengers(c.Scenario).ToDictionary(p => p.Id);

        // Everyone is served exactly once, at the right floors, including anyone left behind or bypassed.
        Assert.Equal((passengers.Count, 0), (result.Delivered, result.Unserved));
        Assert.Equal(passengers.Keys.Order(), result.Events.OfType<PassengerBoarded>().Select(b => b.PassengerId).Order());
        Assert.Equal(passengers.Keys.Order(), result.Events.OfType<PassengerAlighted>().Select(b => b.PassengerId).Order());

        var owner = new Dictionary<int, int>();        // request → elevator holding an accepted assignment
        var load = new Dictionary<int, int>();         // elevator → riders
        var waitingSince = new Dictionary<int, SimTime>();
        var boardedAt = result.Events.OfType<PassengerBoarded>().Select(b => (b.PassengerId, b.ElevatorId, b.At)).ToHashSet();
        foreach (var e in result.Events)
        {
            switch (e)
            {
                case PassengerArrived a:
                    waitingSince[a.PassengerId] = a.At;
                    break;
                case AssignmentAccepted acc:
                    Assert.False(owner.ContainsKey(acc.RequestId), $"request {acc.RequestId} owned twice");
                    owner[acc.RequestId] = acc.ElevatorId;
                    break;
                case AssignmentReleased rel:
                    Assert.Equal(rel.ElevatorId, owner[rel.RequestId]);
                    owner.Remove(rel.RequestId);
                    break;
                case PassengerLeftBehind left:
                    Assert.Equal(left.ElevatorId, owner[left.PassengerId]);
                    owner.Remove(left.PassengerId);
                    break;
                case PassengerBoarded b:
                    var p = passengers[b.PassengerId];
                    Assert.Equal(b.ElevatorId, owner[b.PassengerId]); // only the assigned elevator
                    Assert.Equal(p.From, b.Floor);
                    // Arrival order per floor: nobody who called earlier, for the same floor, direction and elevator, is
                    // left waiting. One door opening is one batch: an earlier caller boarding at the same instant is not
                    // skipped (a rebalance can hand them over as the doors open, so they board second in the batch).
                    Assert.DoesNotContain(waitingSince.Keys, id => id != p.Id
                        && passengers[id].From == p.From && passengers[id].Direction == p.Direction
                        && owner.GetValueOrDefault(id, -1) == b.ElevatorId && waitingSince[id] < waitingSince[p.Id]
                        && !boardedAt.Contains((id, b.ElevatorId, b.At)));
                    owner.Remove(b.PassengerId);
                    waitingSince.Remove(b.PassengerId);
                    load[b.ElevatorId] = load.GetValueOrDefault(b.ElevatorId) + 1;
                    Assert.True(load[b.ElevatorId] <= c.Scenario.Capacity, "over capacity");
                    break;
                case PassengerAlighted al:
                    Assert.Equal(passengers[al.PassengerId].To, al.Floor);
                    load[al.ElevatorId]--;
                    break;
            }
        }

        // No fault was injected, so the watchdog (on by default, T = 30 s) never fires (ADR-013).
        Assert.DoesNotContain(result.Events, e => e is FaultDetected);

        // Wait attribution: every passenger's buckets sum exactly to their wait.
        foreach (var r in PassengerLifecycle.Analyze(result.Events))
            Assert.Equal(r.WaitS!.Value, r.WaitBuckets.Values.Sum(), 6);
    });

    [Property(MaxTest = 40)]
    public Property Same_seed_gives_identical_log_at_any_tick_size() => Prop.ForAll(CaseGen().ToArbitrary(), Gen.Elements(1L, 250L, 30_000L).ToArbitrary(), (c, tick) =>
    {
        var reference = Repo.ToJsonl(SimEngine.Run(c.Scenario, c.Strategy));
        Assert.Equal(reference, Repo.ToJsonl(SimEngine.Run(c.Scenario, c.Strategy, tick)));
    });
}
