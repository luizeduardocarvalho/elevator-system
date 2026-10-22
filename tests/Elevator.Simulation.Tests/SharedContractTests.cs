using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Simulation.Tests;

/// <summary>The shared v3 contract (docs/contract-v3.md): types round-trip, seams preserve behaviour.</summary>
public class SharedContractTests
{
    [Fact]
    public void New_events_round_trip_through_the_jsonl_log()
    {
        var t = new SimTime(4_200);
        DomainEvent[] events =
        [
            new MaintenanceRequested(t, 1), new MaintenanceEntered(t, 1, 3), new MaintenanceExited(t, 1),
            new EmergencyStopped(t, 2, 7), new EmergencyCleared(t, 2),
            new FaultDetected(t, 3, 9, 60_000), new FaultRecovered(t, 3),
            new AccessDenied(t, 10, 2, "floor-restriction", "E2 does not serve floor 13"),
            new AccessDenied(t, 11, null, "vip", "no elevator allowed"),
            new FaultCommandApplied(t, 0, FaultKind.Stuck, 120_000), new FaultCommandApplied(t, 0, FaultKind.MaintenanceOn, null),
            new DispatchEvaluated(t, 12, [new CandidateCost(0, 15.5, "eta"), new CandidateCost(1, null, "ineligible")], 0),
            new DispatchEvaluated(t, 13, [new CandidateCost(0, null)], null),
            new AssignmentReleased(t, 12, 0, AssignmentReason.Fault), new AssignmentReleased(t, 12, 0, AssignmentReason.Emergency),
        ];
        using var writer = new StringWriter();
        JsonlEventLog.Write(writer, events);
        Assert.Equal(events, JsonlEventLog.Read(new StringReader(writer.ToString())));
        Assert.Contains("\"kind\":\"MAINTENANCE_ON\"", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_defaults_are_normal_standard_and_every_floor()
    {
        var s = SimEngine.Create(Repo.Load("up-peak.yaml")).Elevators[0];
        Assert.Equal((ElevatorMode.Normal, "standard"), (s.Mode, s.TypeName));
        Assert.True(Enumerable.Range(1, 20).All(s.Serves));
    }

    [Fact]
    public void Fault_schedule_is_loaded_and_ordered()
    {
        var s = ScenarioLoader.Load("""
            building: { elevators: 4 }
            faults:
              - { atS: 800, elevator: 3, kind: stuck, durationS: 120 }
              - { atS: 300, elevator: 2, kind: maintenance-on }
              - { atS: 420, elevator: 1, kind: emergency-stop }
            """).Scenario!;
        Assert.Equal(
            [new FaultCommand(new SimTime(300_000), 2, FaultKind.MaintenanceOn), new FaultCommand(new SimTime(420_000), 1, FaultKind.EmergencyStop),
             new FaultCommand(new SimTime(800_000), 3, FaultKind.Stuck, 120_000)],
            s.FaultSchedule);
    }

    [Theory]
    [InlineData("{ atS: 1, elevator: 4, kind: stuck }", "faults[0]: elevator 4 does not exist (0..3)")]
    [InlineData("{ atS: 1, elevator: 0, kind: explode }", "faults[0]: unknown kind 'explode'")]
    [InlineData("{ atS: -1, elevator: 0, kind: stuck }", "faults[0]: 'atS' must be >= 0")]
    [InlineData("{ atS: 1, elevator: 0, kind: emergency-stop, durationS: 5 }", "faults[0]: 'durationS' only applies to kind stuck")]
    [InlineData("{ elevator: 0, kind: stuck }", "faults[0]: 'atS', 'elevator' and 'kind' are required")]
    public void Invalid_fault_entries_are_reported(string entry, string expected) =>
        Assert.Contains(ScenarioLoader.Load($"building: {{ elevators: 4 }}\nfaults:\n  - {entry}").Errors, e => e.StartsWith(expected, StringComparison.Ordinal));

    [Fact]
    public void No_op_injector_changes_nothing()
    {
        var scenario = Repo.Load("lunch.yaml");
        var reference = Repo.ToJsonl(SimEngine.Run(scenario));
        var engine = SimEngine.Create(scenario);
        Assert.True(engine.FaultInjector.IsNoOp);
        engine.AdvanceTo(new SimTime(300_000));
        engine.FaultInjector.EmergencyStop(1, engine.Now);
        engine.FaultInjector.Stick(2, engine.Now, 60_000);
        while (!engine.Finished) engine.AdvanceTo(engine.Now.AddMs(100));
        Assert.Equal(reference, Repo.ToJsonl(engine.Finish()));
    }

    [Property(MaxTest = 40)]
    public Property Stepping_in_any_steps_gives_the_same_log_as_run() => Prop.ForAll(
        (from n in Gen.Choose(1, 30) from a in Gen.ArrayOf(Gen.Choose(1, 90_000), n) select a).ToArbitrary(), Gen.Elements("up-peak.yaml", "lunch.yaml", "v1-basic.yaml").ToArbitrary(), (steps, name) =>
        {
            var scenario = Repo.Load(name);
            var reference = Repo.ToJsonl(SimEngine.Run(scenario));
            var engine = SimEngine.Create(scenario);
            for (var i = 0; !engine.Finished; i++) engine.AdvanceTo(engine.Now.AddMs(steps[i % steps.Length]));
            Assert.Equal(reference, Repo.ToJsonl(engine.Finish()));
        });

    [Fact]
    public void Posted_messages_are_delivered_at_now_after_what_is_already_due_deterministically()
    {
        string RunWithPost()
        {
            var engine = SimEngine.Create(Repo.Load("v1-basic.yaml"));
            engine.AdvanceTo(new SimTime(5_000));
            var late = Request.Create(2, 6, engine.Now, 10, id: 99).Value!;
            engine.Post(Address.Passengers, new PassengerArrives(late));
            engine.AdvanceTo(engine.Now); // deliver what was posted
            var arrived = Assert.IsType<PassengerArrived>(engine.Log.Last(e => e is PassengerArrived));
            Assert.Equal((99, 5_000L), (arrived.PassengerId, arrived.At.Ms));
            while (!engine.Finished && engine.Now < new SimTime(300_000)) engine.AdvanceTo(engine.Now.AddMs(1_000));
            return Repo.ToJsonl(engine.Finish());
        }
        Assert.Equal(RunWithPost(), RunWithPost());
    }

    [Theory]
    [InlineData("cost")]
    [InlineData("nearest")]
    public void Dispatch_evaluations_list_every_candidate_and_match_each_proposal(string dispatch)
    {
        var scenario = Repo.Load("lunch.yaml") with { Dispatch = dispatch };
        var plain = SimEngine.Run(scenario);
        var engine = SimEngine.Create(scenario, options: new SimEngineOptions(EmitDispatchEvaluations: true));
        while (!engine.Finished) engine.AdvanceTo(engine.Now.AddMs(100));
        var explained = engine.Finish().Events;

        // Explanations are extra lines only: removing them gives the plain log back.
        Assert.Equal(Repo.ToJsonl(plain), Repo.ToJsonl(new SimulationResult(explained.Where(e => e is not DispatchEvaluated).ToList(), 0, 0, default)));
        Assert.DoesNotContain(plain.Events, e => e is DispatchEvaluated);

        var proposals = explained.OfType<AssignmentProposed>().ToList();
        var evaluations = explained.OfType<DispatchEvaluated>().Where(d => d.WinnerId is not null).ToList();
        Assert.Equal(proposals.Count, evaluations.Count);
        foreach (var (p, d) in proposals.Zip(evaluations))
        {
            Assert.Equal((p.RequestId, p.ElevatorId), (d.RequestId, d.WinnerId!.Value));
            Assert.Equal(4, d.Candidates.Count);
            Assert.Equal(Math.Round(p.Cost, 3), Math.Round(d.Candidates.Single(c => c.ElevatorId == p.ElevatorId).Cost!.Value, 3));
        }
    }

    [Fact]
    public void Waiting_counts_are_exposed_per_floor_and_direction()
    {
        var engine = SimEngine.Create(Repo.Load("v1-basic.yaml"));
        engine.AdvanceTo(new SimTime(2_500)); // P1 (5↑), P2 (3↓), P3 (7↑) waiting
        Assert.Equal(new Dictionary<(int, Direction), int> { [(5, Direction.Up)] = 1, [(3, Direction.Down)] = 1, [(7, Direction.Up)] = 1 },
            engine.Waiting);
    }

    // ---- v3.1 amendment: optional fields, written only when set ----

    [Fact]
    public void Passenger_attributes_round_trip_and_are_absent_when_unset()
    {
        var t = new SimTime(1_000);
        DomainEvent[] events =
        [
            new PassengerArrived(t, 1, 1, 9, Load: 4, Vip: true, AccessLevel: 2),
            new PassengerArrived(t, 2, 3, 7),
        ];
        using var writer = new StringWriter();
        JsonlEventLog.Write(writer, events);
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(events, JsonlEventLog.Read(new StringReader(writer.ToString())));
        Assert.Contains("\"load\":4", lines[0], StringComparison.Ordinal);
        Assert.Equal("""{"seq":1,"type":"PassengerArrived","t":1000,"passengerId":2,"from":3,"to":7}""", lines[1]); // unchanged shape
        var read = (PassengerArrived)JsonlEventLog.Deserialize(lines[1]);
        Assert.Equal((1, false), (read.LoadUnits, read.IsVip));
    }

    [Fact]
    public void Fleet_round_trips_and_is_absent_for_the_default_building()
    {
        FleetEntry[] fleet = [new(0, "local", 10, 0.8, 0b1111_1110), new(1, "express", 12, 0.8, 0b1000_0010), new(2, "freight", 30, 1.0, 0b1111_1110)];
        DomainEvent[] events =
        [
            new SimulationStarted(SimTime.Zero, "typed", 1, 7, "look", 10, 3, "cost", 10, 0.8, "next-trip", fleet),
            new SimulationStarted(SimTime.Zero, "plain", 1, 10, "look", 10, 4, "cost", 10, 0.8),
        ];
        using var writer = new StringWriter();
        JsonlEventLog.Write(writer, events);

        Assert.Equal(events, JsonlEventLog.Read(new StringReader(writer.ToString())));
        Assert.DoesNotContain("fleet", writer.ToString().Split('\n')[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Logs_written_before_the_amendment_read_with_nulls_and_reserialise_unchanged()
    {
        // Lines from a v2-medium log of lunch.yaml, before the v3.1 fields existed.
        const string started = """{"seq":0,"type":"SimulationStarted","t":0,"scenario":"lunch","seed":1,"floors":20,"scheduling":"look","passengers":264,"elevators":4,"dispatch":"cost","capacity":10,"bypassThreshold":0.8,"capacityMode":"next-trip"}""";
        const string arrived = """{"seq":1,"type":"PassengerArrived","t":1430,"passengerId":1,"from":16,"to":1}""";

        var s = Assert.IsType<SimulationStarted>(JsonlEventLog.Deserialize(started));
        var a = Assert.IsType<PassengerArrived>(JsonlEventLog.Deserialize(arrived));
        Assert.Null(s.Fleet);
        Assert.Equal((null, null, null, 1, false), (a.Load, a.Vip, a.AccessLevel, a.LoadUnits, a.IsVip));
        Assert.Equal(started, JsonlEventLog.Serialize(s, 0));
        Assert.Equal(arrived, JsonlEventLog.Serialize(a, 1));
    }
}
