using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Simulation.Tests;

/// <summary>Maintenance with riders on board, end to end (ADR-013).</summary>
public class MaintenanceScenarioTests
{
    private const int E = 1;

    [Fact]
    public void Pickups_are_reassigned_riders_delivered_then_it_parks_and_comes_back()
    {
        var engine = SimEngine.Create(Repo.Load("lunch.yaml"), options: new SimEngineOptions(FaultInjector: SimFaultInjector.Factory));
        // Find a moment when E1 has people on board and people waiting for it.
        while (engine.Elevators[E] is not { Riders.Count: >= 2 } s || !s.HallCalls.Any(c => c.Requests.Count > 0))
            engine.AdvanceTo(engine.Now.AddMs(1_000));
        var at = engine.Now;
        var riders = engine.Elevators[E].Riders.Select(r => r.Id).ToHashSet();
        var pickups = engine.Elevators[E].HallCalls.SelectMany(c => c.Requests).Select(r => r.Id).ToHashSet();

        engine.FaultInjector.RequestMaintenance(E, at);
        engine.FaultInjector.EndMaintenance(E, at.AddMs(600_000));
        while (!engine.Finished) engine.AdvanceTo(engine.Now.AddMs(100));
        var events = engine.Finish().Events;
        Assert.Equal(0, engine.Finish().Unserved);

        var requested = events.OfType<MaintenanceRequested>().Single();
        var entered = events.OfType<MaintenanceEntered>().Single();
        var exited = events.OfType<MaintenanceExited>().Single();
        Assert.Equal((at, E), (requested.At, requested.ElevatorId));

        // Its pickups are handed back at once and served by other elevators.
        Assert.Equal(pickups, events.OfType<AssignmentReleased>().Where(r => r.ElevatorId == E && r.Reason == AssignmentReason.Maintenance)
            .Select(r => r.RequestId).ToHashSet());
        Assert.All(events.OfType<AssignmentReleased>().Where(r => r.Reason == AssignmentReason.Maintenance), r => Assert.Equal(at, r.At));
        Assert.All(events.OfType<PassengerBoarded>().Where(b => pickups.Contains(b.PassengerId)), b => Assert.NotEqual(E, b.ElevatorId));

        // Its riders are delivered by it; only then does it park, and nothing is assigned to it until it is back.
        var lastAlight = events.OfType<PassengerAlighted>().Where(a => riders.Contains(a.PassengerId)).ToList();
        Assert.Equal(riders.Count, lastAlight.Count);
        Assert.All(lastAlight, a => Assert.Equal(E, a.ElevatorId));
        Assert.True(entered.At >= lastAlight.Max(a => a.At));
        Assert.DoesNotContain(events, ev => ev is AssignmentAccepted { ElevatorId: E } a && a.At >= requested.At && a.At < exited.At);
        Assert.DoesNotContain(events, ev => ev is PassengerBoarded { ElevatorId: E } b && b.At > requested.At && b.At < exited.At);
        Assert.Contains(events, ev => ev is AssignmentAccepted { ElevatorId: E } a && a.At >= exited.At); // back in service
        Assert.Equal(new SimTime(at.Ms + 600_000), exited.At);
        Assert.Equal([FaultKind.MaintenanceOn, FaultKind.MaintenanceOff], events.OfType<FaultCommandApplied>().Select(f => f.Kind));
    }

    [Fact]
    public void A_single_elevator_building_waits_for_the_end_of_maintenance_and_loses_nobody()
    {
        var scenario = Repo.Load("v1-basic.yaml") with
        {
            Faults = [new FaultCommand(new SimTime(1_000), 0, FaultKind.MaintenanceOn), new FaultCommand(new SimTime(200_000), 0, FaultKind.MaintenanceOff)],
        };
        var result = SimEngine.Run(scenario);
        Assert.Equal(0, result.Unserved);
        Assert.Contains(result.Events, e => e is DispatchDeferred); // nobody to take them meanwhile
        Assert.All(result.Events.OfType<PassengerBoarded>(), b => Assert.True(b.At.Ms >= 200_000)); // the first pickup was handed back at 1 s
    }
}
