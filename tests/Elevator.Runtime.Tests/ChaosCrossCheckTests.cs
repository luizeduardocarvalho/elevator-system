using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Metrics;
using Elevator.Simulation;

namespace Elevator.Runtime.Tests;

/// <summary>
/// Outcome equivalence under faults (ADR-013): a light chaos scenario through the deterministic sim engine and through
/// real actors on an accelerated clock, with the same fault schedule (sim engine: the scenario's schedule; actors: the
/// runtime injector on the system clock). Logs differ by thread interleaving and timer jitter, so outcomes are compared.
/// </summary>
public class ChaosCrossCheckTests
{
    private static Scenario LightChaos() => new(
        "light-chaos", 7, 20, ElevatorTiming.Default, "look", SimTime.FromSeconds(3_600), [], null,
        Elevators: 4, Capacity: 10, BypassThreshold: 0.8, Dispatch: "cost",
        Profile: new TrafficProfile([new RatePoint(0, 6), new RatePoint(600, 6)], new TrafficMix(0.4, 0.4, 0.2)),
        Faults:
        [
            new FaultCommand(SimTime.FromSeconds(120), 1, FaultKind.MaintenanceOn),
            new FaultCommand(SimTime.FromSeconds(200), 2, FaultKind.EmergencyStop),
            new FaultCommand(SimTime.FromSeconds(260), 2, FaultKind.EmergencyClear),
            new FaultCommand(SimTime.FromSeconds(300), 1, FaultKind.MaintenanceOff),
            new FaultCommand(SimTime.FromSeconds(320), 3, FaultKind.Stuck, 90_000),
        ]);

    [Fact]
    public async Task Actor_runtime_and_sim_engine_reach_equivalent_outcomes_under_faults()
    {
        var scenario = LightChaos();
        var passengers = ScenarioRunner.BuildPassengers(scenario);
        var simEvents = SimEngine.Run(scenario).Events;
        var sim = ReportBuilder.Build(simEvents);

        await using var system = ElevatorSystem.Start(RuntimeOptions.From(scenario, speed: 200));
        foreach (var f in scenario.FaultSchedule) system.FaultInjector.Inject(f);
        await system.PlayPassengers(passengers);
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (system.PassengersForInspection.Delivered < passengers.Count && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        while (system.Events.OfType<FaultCommandApplied>().Count() < scenario.FaultSchedule.Count && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Null(system.Fault);

        var events = system.Events.OrderBy(e => e.At).ToList();
        var actors = ReportBuilder.Build(events);

        // Everyone delivered, in both, with every invariant intact (including: nothing accepted while out of service).
        Assert.Equal((passengers.Count, passengers.Count), (sim.Delivered, actors.Delivered));
        Invariants.Check(system.Events, scenario.Capacity);
        Invariants.CheckModes(system.Events);

        // The same operations happened: every command took effect, the maintenance drained and parked, the emergency
        // stopped and cleared; the same pickups-handed-back mechanism ran.
        static IEnumerable<(int, FaultKind)> Applied(IEnumerable<DomainEvent> log) =>
            log.OfType<FaultCommandApplied>().Select(f => (f.ElevatorId, f.Kind)).Order();
        Assert.Equal(Applied(simEvents), Applied(events));
        foreach (var log in new[] { simEvents, events })
        {
            Assert.Single(log.OfType<MaintenanceEntered>(), m => m.ElevatorId == 1);
            Assert.Single(log.OfType<MaintenanceExited>(), m => m.ElevatorId == 1);
            Assert.Single(log.OfType<EmergencyStopped>(), m => m.ElevatorId == 2);
            Assert.Single(log.OfType<EmergencyCleared>(), m => m.ElevatorId == 2);
            Assert.Equal(log.OfType<FaultDetected>().Count(), log.OfType<FaultRecovered>().Count()); // every fault recovered
        }
        // 180 s and 60 s in the sim. The runtime injector waits on the wall clock, so a command can land a little late
        // under load (5 ms of wall time is 1 s of sim time at 200×).
        Assert.InRange(actors.Ops.MaintenanceS, sim.Ops.MaintenanceS - 3, sim.Ops.MaintenanceS + 3);
        Assert.InRange(actors.Ops.EmergencyS, sim.Ops.EmergencyS - 3, sim.Ops.EmergencyS + 3);

        // Light load: the experience agrees within timer jitter.
        Assert.InRange(actors.Wait.Avg!.Value, sim.Wait.Avg!.Value * 0.7 - 3, sim.Wait.Avg!.Value * 1.3 + 3);
        Assert.InRange(actors.Ride.Avg!.Value, sim.Ride.Avg!.Value * 0.8 - 3, sim.Ride.Avg!.Value * 1.2 + 3);
    }
}
