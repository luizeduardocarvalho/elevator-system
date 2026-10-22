using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Lab;
using Elevator.Simulation;

namespace Elevator.Runtime.Tests;

/// <summary>
/// The steady-state ramp's foundations (ADR-016): a frozen clock, the busiest moment of a scenario, handing the
/// simulation over to the actor runtime, and retiring requests on acceptance so the seeded backlog stays put.
/// </summary>
public class SteadyStateTests
{
    private static Scenario Lunch() => ScenarioLoader.LoadFile(Path.Combine(Root(), "scenarios", "lunch.yaml")).Scenario!;

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ElevatorSystem.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    private static (int Floor, ElevatorState State, string Riders, string Pickups) Shape(ElevatorSnapshot s) =>
        (s.CurrentFloor, s.State, string.Join(",", s.Riders.Select(r => r.Id).Order()),
            string.Join(",", s.HallCalls.SelectMany(c => c.Requests).Select(r => r.Id).Order()));

    private static List<ElevatorHandler> Elevators(Handover h) =>
        [.. h.Handlers.OfType<ElevatorHandler>().OrderBy(e => e.Elevator.Id)];

    [Fact]
    public void A_frozen_clock_stands_still_and_nothing_comes_due()
    {
        var clock = new RealClock(speed: 0, start: new Instant(1_500_000));
        Thread.SpinWait(100_000);
        Assert.Equal(new Instant(1_500_000), clock.Now);
        Assert.Equal(Timeout.InfiniteTimeSpan, clock.WallUntil(new Instant(1_500_001)));
        Assert.Equal(TimeSpan.Zero, clock.WallUntil(new Instant(1_500_000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RealClock(speed: -1));
    }

    [Fact]
    public void The_peak_moment_is_deterministic_and_busy()
    {
        var a = PeakMoment.Find(Lunch());
        var b = PeakMoment.Find(Lunch());
        Assert.Equal((a.At, a.Outstanding), (b.At, b.Outstanding));
        Assert.True(a.Outstanding >= 10, $"only {a.Outstanding} outstanding at the peak");
        Assert.Equal(a.Outstanding, a.PerElevator.Sum(p => p.Riders + p.Pickups));
    }

    [Fact]
    public async Task The_runtime_takes_over_exactly_where_the_simulation_was()
    {
        var lunch = Lunch();
        var peak = PeakMoment.Find(lunch);
        var handover = PeakMoment.HandOverAt(lunch, peak.At);
        var reference = SimEngine.Create(lunch);
        reference.AdvanceTo(peak.At);

        await using (var system = ElevatorSystem.FromEngine(handover, lunch.ToRuntimeOptions(speed: 0) with { KeepEvents = false }))
        {
            Assert.Equal(peak.At, system.Clock.Now);
            await Task.Delay(100); // frozen: nothing moves
        }
        Assert.Equal(reference.Elevators.Select(Shape), Elevators(handover).Select(e => Shape(e.Elevator.Snapshot())));
    }

    [Fact]
    public void A_handed_over_engine_refuses_to_run()
    {
        var engine = SimEngine.Create(Lunch());
        engine.AdvanceTo(new Instant(60_000));
        engine.HandOver();
        Assert.Throws<InvalidOperationException>(() => engine.AdvanceTo(new Instant(61_000)));
        Assert.Throws<InvalidOperationException>(() => engine.HandOver());
    }

    /// <summary>
    /// Steady state: with the building frozen at its peak and every request retired on acceptance, 500 requests from 8
    /// threads are all assigned, and afterwards every elevator holds exactly its seeded riders and pickups at the same
    /// floor again.
    /// </summary>
    [Fact]
    public async Task Retire_on_accept_keeps_the_seeded_backlog_constant()
    {
        var lunch = Lunch();
        var peak = PeakMoment.Find(lunch);
        var handover = PeakMoment.HandOverAt(lunch, peak.At);
        var seeded = Elevators(handover).Select(e => Shape(e.Elevator.Snapshot())).ToList();
        var passengers = handover.Handlers.OfType<PassengersHandler>().Single();

        await using (var system = ElevatorSystem.FromEngine(handover,
                         lunch.ToRuntimeOptions(speed: 0) with { KeepEvents = false, RetireOnAccept = true }))
        {
            var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
            {
                var rng = new Random(t);
                for (var i = 0; i < 500 / 8 + 1; i++)
                {
                    var id = 1_000_000 + t * 1_000 + i;
                    var from = rng.Next(1, 21);
                    int to;
                    do to = rng.Next(1, 21); while (to == from);
                    await system.AssignRequest(Request.Create(from, to, system.Clock.Now, 20, id: id).Value!);
                }
            })).ToList();
            await Task.WhenAll(tasks);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (passengers.Retired < 8 * (500 / 8 + 1) && DateTime.UtcNow < deadline) await Task.Delay(10);
            await Task.Delay(100);
            Assert.Null(system.Fault);
            Assert.Equal(0, system.InFlight);
        }

        Assert.Equal(8 * (500 / 8 + 1), passengers.Retired);
        // The backlog (positions, riders, pickups), which sets the cost of every decision, is exactly the seeded one.
        // The phase may change once at the frozen instant: doors closing at a floor reopen for a pickup there, an idle
        // elevator commits to a direction (ADR-016); with no time passing, nothing moves further.
        static (int, string, string) Backlog((int Floor, ElevatorState State, string Riders, string Pickups) e) => (e.Floor, e.Riders, e.Pickups);
        Assert.Equal(seeded.Select(Backlog), Elevators(handover).Select(e => Backlog(Shape(e.Elevator.Snapshot()))));
    }
}
