using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Metrics;
using Elevator.Simulation;

namespace Elevator.Runtime.Tests;

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

        Assert.Equal((passengers.Count, passengers.Count), (sim.Delivered, actors.Delivered));
        Invariants.Check(system.Events, scenario.Capacity);
        Invariants.CheckModes(system.Events);

        static IEnumerable<(int, FaultKind)> Applied(IEnumerable<DomainEvent> log) =>
            log.OfType<FaultCommandApplied>().Select(f => (f.ElevatorId, f.Kind)).Order();
        Assert.Equal(Applied(simEvents), Applied(events));
        foreach (var log in new[] { simEvents, events })
        {
            Assert.Single(log.OfType<MaintenanceEntered>(), m => m.ElevatorId == 1);
            Assert.Single(log.OfType<MaintenanceExited>(), m => m.ElevatorId == 1);
            Assert.Single(log.OfType<EmergencyStopped>(), m => m.ElevatorId == 2);
            Assert.Single(log.OfType<EmergencyCleared>(), m => m.ElevatorId == 2);
            Assert.Equal(log.OfType<FaultDetected>().Count(), log.OfType<FaultRecovered>().Count());
        }
        Assert.InRange(actors.Ops.MaintenanceS, sim.Ops.MaintenanceS - 3, sim.Ops.MaintenanceS + 3);
        Assert.InRange(actors.Ops.EmergencyS, sim.Ops.EmergencyS - 3, sim.Ops.EmergencyS + 3);

        Assert.InRange(actors.Wait.Avg!.Value, sim.Wait.Avg!.Value * 0.7 - 3, sim.Wait.Avg!.Value * 1.3 + 3);
        Assert.InRange(actors.Ride.Avg!.Value, sim.Ride.Avg!.Value * 0.8 - 3, sim.Ride.Avg!.Value * 1.2 + 3);
    }
}
