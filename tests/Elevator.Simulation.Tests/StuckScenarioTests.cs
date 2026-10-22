using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Runtime;

namespace Elevator.Simulation.Tests;

public class StuckScenarioTests
{
    [Fact]
    public void Detected_within_T_reassigned_recovered_and_every_rider_delivered()
    {
        const int E = 2;
        var engine = SimEngine.Create(Repo.Load("lunch.yaml"), options: new SimEngineOptions(FaultInjector: SimFaultInjector.Factory));
        while (engine.Elevators[E] is not { Riders.Count: > 0, State: ElevatorState.MovingUp or ElevatorState.MovingDown } s
               || !s.HallCalls.Any(c => c.Requests.Count > 0))
            engine.AdvanceTo(engine.Now.AddMs(500));
        var stuckAt = engine.Now;
        var riders = engine.Elevators[E].Riders.Select(r => r.Id).ToHashSet();
        var pickups = engine.Elevators[E].HallCalls.SelectMany(c => c.Requests).Select(r => r.Id).ToHashSet();
        engine.FaultInjector.Stick(E, stuckAt);
        engine.FaultInjector.Unstick(E, stuckAt.AddMs(120_000));
        while (!engine.Finished) engine.AdvanceTo(engine.Now.AddMs(100));
        var result = engine.Finish();
        var events = result.Events;

        var detected = events.OfType<FaultDetected>().Single();
        Assert.Equal(E, detected.ElevatorId);
        Assert.InRange(detected.At.Ms - stuckAt.Ms, 1, RuntimeOptions.DefaultWatchdogTimeoutMs);
        Assert.True(detected.SilentForMs >= RuntimeOptions.DefaultWatchdogTimeoutMs);

        var released = events.OfType<AssignmentReleased>().Where(r => r.Reason == AssignmentReason.Fault).ToList();
        Assert.True(pickups.IsSubsetOf(released.Select(r => r.RequestId)));
        Assert.All(released, r => Assert.Equal(detected.At, r.At));
        Assert.All(events.OfType<PassengerBoarded>().Where(b => released.Any(r => r.RequestId == b.PassengerId)), b => Assert.NotEqual(E, b.ElevatorId));

        var recovered = events.OfType<FaultRecovered>().Single();
        Assert.InRange(recovered.At.Ms - (stuckAt.Ms + 120_000), 0, ElevatorTiming.Default.FloorTravelMs);
        Assert.DoesNotContain(events, e => e is AssignmentAccepted { ElevatorId: E } a && a.At >= detected.At && a.At < recovered.At);
        Assert.All(events.OfType<PassengerAlighted>().Where(a => riders.Contains(a.PassengerId)), a => Assert.Equal(E, a.ElevatorId));
        Assert.Equal(0, result.Unserved);
    }

    [Fact]
    public void A_quiet_run_never_trips_the_watchdog() =>
        Assert.DoesNotContain(SimEngine.Run(Repo.Load("down-peak.yaml") with { Dispatch = "nearest" }).Events,
            e => e is FaultDetected);

    [Theory]
    [InlineData("watchdog: { timeoutS: 0 }", "watchdog.timeoutS must be > 0")]
    public void Invalid_watchdog_config_is_reported(string yaml, string expected) =>
        Assert.Contains(ScenarioLoader.Load(yaml).Errors, e => e.StartsWith(expected, StringComparison.Ordinal));

    [Fact]
    public void The_watchdog_timeout_is_configurable()
    {
        Assert.Equal(45_000, ScenarioLoader.Load("watchdog: { timeoutS: 45 }").Scenario!.WatchdogTimeoutMs);
        Assert.Equal(RuntimeOptions.DefaultWatchdogTimeoutMs, ScenarioLoader.Load("name: x").Scenario!.WatchdogTimeoutMs);
    }
}
