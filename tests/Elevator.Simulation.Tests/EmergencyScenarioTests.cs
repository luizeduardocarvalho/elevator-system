using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Simulation.Tests;

/// <summary>Emergency stops in the simulator (ADR-013): they preempt what is queued, and riders are kept safe.</summary>
public class EmergencyScenarioTests
{
    [Fact]
    public void An_emergency_behind_50_queued_commands_takes_effect_first()
    {
        var engine = SimEngine.Create(Repo.Load("lunch.yaml"), options: new SimEngineOptions(FaultInjector: SimFaultInjector.Factory));
        engine.AdvanceTo(new SimTime(120_000));
        for (var i = 0; i < 50; i++) // 50 assignments queued for E0 at this instant, ahead of the emergency
            engine.Post(Address.Elevator(0), new Assign(Request.Create(1 + i % 19, 20, engine.Now, 20, id: 90_000 + i).Value!, null, Forced: true));
        engine.FaultInjector.EmergencyStop(0, engine.Now);
        var from = engine.Log.Count;
        engine.AdvanceTo(engine.Now);

        var log = engine.Log.Skip(from).ToList();
        var stop = log.FindIndex(e => e is FaultCommandApplied { ElevatorId: 0, Kind: FaultKind.EmergencyStop });
        Assert.True(stop >= 0);
        Assert.DoesNotContain(log.Take(stop), e => e is AssignmentAccepted or AssignmentRejected);
        var answers = log.Where(e => e is AssignmentAccepted { RequestId: >= 90_000 } or AssignmentRejected { RequestId: >= 90_000 }).ToList();
        Assert.Equal(50, answers.Count);
        Assert.All(answers, a => Assert.Equal(AssignmentReason.Emergency, Assert.IsType<AssignmentRejected>(a).Reason));
    }

    [Fact]
    public void Riders_stay_inside_with_the_doors_closed_until_cleared_and_are_then_delivered()
    {
        var engine = SimEngine.Create(Repo.Load("lunch.yaml"), options: new SimEngineOptions(FaultInjector: SimFaultInjector.Factory));
        while (engine.Elevators[2] is not { Riders.Count: > 0, State: ElevatorState.MovingUp or ElevatorState.MovingDown })
            engine.AdvanceTo(engine.Now.AddMs(500));
        var stoppedAt = engine.Now;
        var riders = engine.Elevators[2].Riders.Select(r => r.Id).ToHashSet();
        engine.FaultInjector.EmergencyStop(2, stoppedAt);
        engine.FaultInjector.ClearEmergency(2, stoppedAt.AddMs(90_000));
        while (!engine.Finished) engine.AdvanceTo(engine.Now.AddMs(100));
        var events = engine.Finish().Events;

        Assert.Equal(0, engine.Finish().Unserved);
        var cleared = events.OfType<EmergencyCleared>().Single();
        Assert.Equal(stoppedAt.Ms + 90_000, cleared.At.Ms);
        var halt = events.OfType<StateChanged>().Single(s => s is { ElevatorId: 2, Trigger: Trigger.EmergencyHalt });
        Assert.True(halt.At.Ms - stoppedAt.Ms <= ElevatorTiming.Default.FloorTravelMs); // the next floor, not further
        Assert.DoesNotContain(events, e => e is DoorsOpened { ElevatorId: 2 } d && d.At > stoppedAt && d.At < cleared.At);
        Assert.All(events.OfType<PassengerAlighted>().Where(a => riders.Contains(a.PassengerId)), a => Assert.True(a.At >= cleared.At));
        Assert.DoesNotContain(events, e => e is AssignmentAccepted { ElevatorId: 2 } a && a.At >= stoppedAt && a.At < cleared.At);
    }

    /// <summary>
    /// Emergency commands keep their posting order among themselves (ADR-015): a clear posted before a stop at the same
    /// instant is applied first (a no-op), then the stop, so the elevator ends up stopped.
    /// </summary>
    [Fact]
    public void A_clear_posted_before_a_stop_at_the_same_instant_leaves_the_elevator_stopped()
    {
        var engine = SimEngine.Create(Repo.Load("lunch.yaml"), options: new SimEngineOptions(FaultInjector: SimFaultInjector.Factory));
        engine.AdvanceTo(new SimTime(120_000));
        var from = engine.Log.Count;
        engine.FaultInjector.ClearEmergency(0, engine.Now);
        engine.FaultInjector.EmergencyStop(0, engine.Now);
        engine.AdvanceTo(engine.Now.AddMs(1_000));

        var log = engine.Log.Skip(from).ToList();
        Assert.Equal([FaultKind.EmergencyStop], log.OfType<FaultCommandApplied>().Where(f => f.ElevatorId == 0).Select(f => f.Kind));
        Assert.DoesNotContain(log, e => e is EmergencyCleared { ElevatorId: 0 });
    }
}
