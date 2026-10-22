using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Metrics;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Simulation.Tests;

/// <summary>
/// The Hard stage's invariants (ADR-012, ADR-013) over random buildings, elevator types, access policies, passengers and
/// fault schedules, checked on the event log:
/// <list type="bullet">
/// <item>no assignment is accepted by an elevator that is draining, parked for maintenance, in an emergency or faulty;</item>
/// <item>no request is lost: each is delivered, or refused by access policy (AccessDenied with no elevator);</item>
/// <item>every rider is delivered, at the right floor, by the elevator they boarded;</item>
/// <item>a VIP-dedicated elevator never carries a non-VIP; a restricted floor is never served to an unauthorised passenger;
/// an elevator never opens at a floor its type does not serve; load never exceeds capacity;</item>
/// <item>wait attribution still sums exactly, and the log is the same at any tick size.</item>
/// </list>
/// </summary>
public class HardStageProperties
{
    public sealed record Case(Scenario Scenario, Strategy Strategy)
    {
        public override string ToString()
        {
            var s = Scenario;
            return $"{Strategy} floors {s.Floors} cap {s.Capacity}×{s.BypassThreshold} latency {s.Latency}\n" +
                   $"fleet: {string.Join("; ", s.EffectiveFleet.Select((t, i) => $"E{i} {t.Name} cap {t.Capacity} serves {FloorMask.Describe(t.ServedFloors, s.Floors)}"))}\n" +
                   $"restricted: {string.Join(", ", s.Access.RestrictedFloors.Select(kv => $"{kv.Key}≥{kv.Value}"))}; vip elevators: {string.Join(",", s.Access.VipElevators)}; vip priority {s.Access.VipPriority}\n" +
                   $"faults: {string.Join("; ", s.FaultSchedule.Select(f => $"{f.At.Ms} E{f.ElevatorId} {f.Kind} {f.DurationMs}"))}\n" +
                   $"passengers: {string.Join("; ", s.Passengers.Select(p => $"{p.At.Ms} {p.From}->{p.To}{(p.Vip ? " vip" : "")} L{p.AccessLevel} x{p.Load}"))}";
        }
    }

    private static Gen<ElevatorType> TypeGen(int floors, int capacity) =>
        from kind in Gen.Choose(0, 3)
        select kind switch
        {
            0 => ElevatorType.Standard(ElevatorTiming.Default, capacity),
            // Express: lobby + top half, faster.
            1 => new ElevatorType("express", new ElevatorTiming(1_000, 800, 2_000, 800), capacity,
                FloorMask.Of([1, .. Enumerable.Range(floors / 2 + 1, floors - floors / 2)])),
            // Low-rise local: the bottom half (+1).
            2 => new ElevatorType("low", ElevatorTiming.Default, capacity, FloorMask.Of(Enumerable.Range(1, floors / 2 + 1))),
            // Freight: slow, long dwell, twice the capacity.
            _ => new ElevatorType("freight", new ElevatorTiming(3_000, 2_000, 6_000, 2_000), capacity * 2),
        };

    private static Gen<PassengerSpec> PassengerGen(int floors) =>
        from at in Gen.Choose(0, 240_000)
        from from_ in Gen.Choose(1, floors)
        from offset in Gen.Choose(1, floors - 1)
        from vip in Gen.Elements(false, false, false, false, true)
        from level in Gen.Elements(0, 0, 0, 1, 2)
        from load in Gen.Elements(1, 1, 1, 1, 2, 3, 12)
        select new PassengerSpec(new SimTime(at), from_, (from_ - 1 + offset) % floors + 1) { Vip = vip, AccessLevel = level, Load = load };

    /// <summary>Commands that always end: maintenance on/off, emergency stop/clear, stuck with a duration or an unstick.</summary>
    private static Gen<FaultCommand[]> FaultsGen(int elevators) =>
        from n in Gen.Choose(0, 4)
        from episodes in Gen.ArrayOf(
            from e in Gen.Choose(0, elevators - 1)
            from kind in Gen.Choose(0, 3)
            from start in Gen.Choose(0, 300_000)
            from length in Gen.Choose(1_000, 150_000)
            select kind switch
            {
                0 => new[] { new FaultCommand(new SimTime(start), e, FaultKind.MaintenanceOn), new FaultCommand(new SimTime(start + length), e, FaultKind.MaintenanceOff) },
                1 => [new FaultCommand(new SimTime(start), e, FaultKind.EmergencyStop), new FaultCommand(new SimTime(start + length), e, FaultKind.EmergencyClear)],
                2 => [new FaultCommand(new SimTime(start), e, FaultKind.Stuck, length)],
                _ => [new FaultCommand(new SimTime(start), e, FaultKind.Stuck), new FaultCommand(new SimTime(start + length), e, FaultKind.Unstick)],
            }, n)
        select episodes.SelectMany(x => x).OrderBy(f => f.At).ToArray();

    internal static Gen<Case> CaseGen() =>
        from floors in Gen.Choose(6, 20)
        from elevators in Gen.Choose(1, 5)
        from capacity in Gen.Choose(3, 8)
        from threshold in Gen.Elements(0.5, 0.8, 1.0)
        from fleet in Gen.ArrayOf(TypeGen(floors, capacity), elevators)
        from snapshotMs in Gen.Elements(0L, 0L, 1_500L)
        from count in Gen.Choose(1, 40)
        from specs in Gen.ArrayOf(PassengerGen(floors), count)
        from restrictedCount in Gen.Choose(0, 2)
        from restricted in Gen.ArrayOf(from f in Gen.Choose(1, floors) from l in Gen.Choose(1, 2) select (f, l), restrictedCount)
        from dedicated in Gen.Elements(-1, -1, 0, elevators - 1)
        from vipPriority in Gen.Elements(false, true)
        from faults in FaultsGen(elevators)
        from dispatch in Gen.Elements("nearest", "cost")
        select new Case(
            new Scenario("hard-prop", 1, floors, ElevatorTiming.Default, SimTime.FromSeconds(200_000), specs, null,
                elevators, capacity, threshold, dispatch, null, new MessageLatency(snapshotMs, 0), faults)
            {
                Fleet = fleet,
                Access = new AccessConfig(restricted.DistinctBy(r => r.f).ToDictionary(r => r.f, r => r.l), dedicated < 0 ? [] : [dedicated], vipPriority),
            },
            new Strategy(dispatch));

    [Property(MaxTest = 150)]
    public Property Invariants_hold_under_types_access_policies_and_faults() => Prop.ForAll(CaseGen().ToArbitrary(), c =>
    {
        var scenario = c.Scenario;
        var result = SimEngine.Run(scenario, c.Strategy);
        var events = result.Events;
        var passengers = ScenarioRunner.BuildPassengers(scenario).ToDictionary(p => p.Id);
        var fleet = scenario.EffectiveFleet;

        // No request lost: delivered, or refused by every elevator's access policies (and then never boarded).
        var refused = events.OfType<AccessDenied>().Where(a => a.ElevatorId is null).Select(a => a.RequestId).ToHashSet();
        var delivered = events.OfType<PassengerAlighted>().Select(a => a.PassengerId).ToList();
        var lost = passengers.Keys.Except(delivered).Except(refused).ToList();
        Assert.True(lost.Count == 0, $"lost {string.Join(",", lost)}:\n" + string.Join("\n", events.Where(e => e switch
        {
            PassengerArrived a => lost.Contains(a.PassengerId),
            PassengerBoarded b => lost.Contains(b.PassengerId),
            PassengerLeftBehind l => lost.Contains(l.PassengerId),
            AssignmentProposed or AssignmentAccepted or AssignmentRejected or AssignmentReleased or AccessDenied or DispatchDeferred =>
                lost.Contains((int)e.GetType().GetProperty("RequestId")!.GetValue(e)!),
            _ => false,
        }).Take(40)));
        Assert.Equal(passengers.Keys.Order(), delivered.Concat(refused).Order());
        Assert.Equal(delivered.Count, delivered.Distinct().Count());
        Assert.DoesNotContain(events.OfType<PassengerBoarded>(), b => refused.Contains(b.PassengerId));
        Assert.Equal(passengers.Count - delivered.Count, result.Unserved);

        var mode = Enumerable.Range(0, scenario.Elevators).ToDictionary(e => e, _ => (Requested: false, Parked: false, Emergency: false, Faulty: false));
        var riding = new Dictionary<int, int>();   // passenger → elevator
        var load = new Dictionary<int, int>();     // elevator → load units
        foreach (var e in events)
        {
            switch (e)
            {
                case MaintenanceRequested m: mode[m.ElevatorId] = mode[m.ElevatorId] with { Requested = true }; break;
                case MaintenanceEntered m: mode[m.ElevatorId] = mode[m.ElevatorId] with { Parked = true }; break;
                case MaintenanceExited m: mode[m.ElevatorId] = mode[m.ElevatorId] with { Requested = false, Parked = false }; break;
                case EmergencyStopped s: mode[s.ElevatorId] = mode[s.ElevatorId] with { Emergency = true }; break;
                case EmergencyCleared s: mode[s.ElevatorId] = mode[s.ElevatorId] with { Emergency = false }; break;
                case FaultDetected f: mode[f.ElevatorId] = mode[f.ElevatorId] with { Faulty = true }; break;
                case FaultRecovered f: mode[f.ElevatorId] = mode[f.ElevatorId] with { Faulty = false }; break;

                case AssignmentAccepted a:
                    Assert.True(mode[a.ElevatorId] == (false, false, false, false), $"E{a.ElevatorId} accepted {a.RequestId} while {mode[a.ElevatorId]}");
                    break;

                case DoorsOpened d:
                    Assert.True(fleet[d.ElevatorId].Serves(d.Floor), $"E{d.ElevatorId} ({fleet[d.ElevatorId].Name}) opened at unserved floor {d.Floor}");
                    Assert.False(mode[d.ElevatorId].Emergency, $"E{d.ElevatorId} opened its doors in an emergency");
                    break;

                case PassengerBoarded b:
                    var p = passengers[b.PassengerId];
                    var r = p.ToRequest();
                    Assert.True(r.Vip || !scenario.Access.VipElevators.Contains(b.ElevatorId), $"non-VIP {p.Id} boarded VIP elevator E{b.ElevatorId}");
                    foreach (var f in new[] { p.From, p.To })
                        if (scenario.Access.RestrictedFloors.TryGetValue(f, out var level))
                            Assert.True(r.AccessLevel >= level, $"passenger {p.Id} (level {r.AccessLevel}) served at restricted floor {f}");
                    Assert.True(fleet[b.ElevatorId].Serves(p.From) && fleet[b.ElevatorId].Serves(p.To));
                    riding[b.PassengerId] = b.ElevatorId;
                    load[b.ElevatorId] = load.GetValueOrDefault(b.ElevatorId) + r.Load;
                    Assert.True(load[b.ElevatorId] <= fleet[b.ElevatorId].Capacity, "over capacity");
                    break;

                case PassengerAlighted al:
                    Assert.Equal(riding[al.PassengerId], al.ElevatorId);
                    Assert.Equal(passengers[al.PassengerId].To, al.Floor);
                    load[al.ElevatorId] -= passengers[al.PassengerId].Load;
                    break;
            }
        }

        foreach (var rec in PassengerLifecycle.Analyze(events).Where(r => r.Delivered))
            Assert.Equal(rec.WaitS!.Value, rec.WaitBuckets.Values.Sum(), 6);
    });

    [Property(MaxTest = 25)]
    public Property Same_log_at_any_tick_size_with_faults() =>
        Prop.ForAll(CaseGen().ToArbitrary(), Gen.Elements(1L, 333L, 20_000L).ToArbitrary(), (c, tick) =>
            Assert.Equal(Repo.ToJsonl(SimEngine.Run(c.Scenario, c.Strategy)), Repo.ToJsonl(SimEngine.Run(c.Scenario, c.Strategy, tick))));
}
