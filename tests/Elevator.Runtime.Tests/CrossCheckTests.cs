using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Lab;
using Elevator.Metrics;
using Elevator.Simulation;

namespace Elevator.Runtime.Tests;

public class CrossCheckTests
{
    private static Scenario Light() => new(
        "light-cross-check", 7, 20, ElevatorTiming.Default, SimTime.FromSeconds(3_600), [], null,
        Elevators: 4, Capacity: 10, BypassThreshold: 0.8, Dispatch: "cost",
        Profile: new TrafficProfile([new RatePoint(0, 6), new RatePoint(600, 6)], new TrafficMix(0.4, 0.4, 0.2)));

    [Fact]
    public async Task Actor_runtime_and_sim_engine_reach_equivalent_outcomes()
    {
        var scenario = Light();
        var passengers = ScenarioRunner.BuildPassengers(scenario);
        var sim = ReportBuilder.Build(SimEngine.Run(scenario).Events);

        await using var system = ElevatorSystem.Start(scenario.ToRuntimeOptions(speed: 200));
        await system.PlayPassengers(passengers);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (system.PassengersForInspection.Delivered < passengers.Count && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Null(system.Fault);

        var events = system.Events.OrderBy(e => e.At).ToList();
        var actors = ReportBuilder.Build(events);

        Assert.Equal(passengers.Count, actors.Delivered);
        var byId = passengers.ToDictionary(p => p.Id);
        Assert.Equal(byId.Keys.Order(), events.OfType<PassengerBoarded>().Select(b => b.PassengerId).Order());
        Assert.All(events.OfType<PassengerAlighted>(), a => Assert.Equal(byId[a.PassengerId].To, a.Floor));
        Invariants.Check(system.Events, scenario.Capacity);

        Assert.Equal(sim.Delivered, actors.Delivered);
        Assert.InRange(actors.Wait.Avg!.Value, sim.Wait.Avg!.Value * 0.7 - 3, sim.Wait.Avg!.Value * 1.3 + 3);
        Assert.InRange(actors.Ride.Avg!.Value, sim.Ride.Avg!.Value * 0.8 - 3, sim.Ride.Avg!.Value * 1.2 + 3);
    }
}
