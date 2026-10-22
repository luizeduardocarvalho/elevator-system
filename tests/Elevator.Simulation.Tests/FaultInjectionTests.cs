using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Simulation.Tests;

public class FaultInjectionTests
{
    private static Scenario Lunch(params FaultCommand[] faults) => Repo.Load("lunch.yaml") with { Faults = faults };

    private static List<FaultCommandApplied> Applied(IEnumerable<DomainEvent> events) => [.. events.OfType<FaultCommandApplied>()];

    [Fact]
    public void The_scenario_schedule_is_applied_at_its_sim_time()
    {
        var result = SimEngine.Run(Lunch(new FaultCommand(new Instant(300_000), 2, FaultKind.Stuck, 20_000)));
        Assert.Equal([new FaultCommandApplied(new Instant(300_000), 2, FaultKind.Stuck, 20_000)], Applied(result.Events));
        Assert.Equal(0, result.Unserved);
    }

    [Fact]
    public void The_live_injector_applies_commands_at_max_of_their_time_and_now()
    {
        var engine = SimEngine.Create(Lunch(), options: new SimEngineOptions(FaultInjector: SimFaultInjector.Factory));
        Assert.False(engine.FaultInjector.IsNoOp);
        engine.AdvanceTo(new Instant(100_000));
        engine.FaultInjector.Stick(1, new Instant(150_000), 5_000);
        engine.FaultInjector.Stick(3, new Instant(40_000), 5_000);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.FaultInjector.Stick(4, engine.Now));
        while (!engine.Finished) engine.AdvanceTo(engine.Now.AddMs(100));
        Assert.Equal([(100_000L, 3), (150_000L, 1)], Applied(engine.Finish().Events).Select(a => (a.At.Ms, a.ElevatorId)));
    }

    [Fact]
    public void A_schedule_for_an_elevator_that_does_not_exist_is_rejected() =>
        Assert.Throws<ArgumentException>(() => SimEngine.Create(Lunch(new FaultCommand(Instant.Zero, 9, FaultKind.Stuck))));

    [Property(MaxTest = 15)]
    public Property A_scheduled_run_gives_the_same_log_at_any_tick_size() => Prop.ForAll(Gen.Elements(1L, 250L, 7_000L, 60_000L).ToArbitrary(), tick =>
    {
        var scenario = Lunch(
            new FaultCommand(new Instant(200_000), 0, FaultKind.Stuck, 45_000),
            new FaultCommand(new Instant(400_000), 1, FaultKind.Stuck),
            new FaultCommand(new Instant(460_000), 1, FaultKind.Unstick));
        Assert.Equal(Repo.ToJsonl(SimEngine.Run(scenario)), Repo.ToJsonl(SimEngine.Run(scenario, tickMs: tick)));
    });
}
