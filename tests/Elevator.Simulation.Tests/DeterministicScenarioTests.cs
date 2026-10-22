using Elevator.Domain.Events;
using Elevator.Metrics;

namespace Elevator.Simulation.Tests;

public class DeterministicScenarioTests
{
    /// <summary>
    /// scenarios/v1-basic.yaml traced by hand (2 s/floor, doors 1 s open + 3 s dwell + 1 s close, start at floor 1):
    ///
    ///  t=0   P1 calls 5↑. Idle at 1, goal 5 → moves up.            t=1 P2 calls 3↓; t=2 P3 calls 7↑ (queued).
    ///  t=8   reaches 5 (4 floors), lantern ↑, doors open at 9     → P1 boards (wait 9), presses 9.
    ///  t=13  doors closed; car call 9 ahead → up. Passes 7 at t=17 without stopping (FIFO: H7↑ is not the goal).
    ///  t=21  reaches 9; next goal = oldest hall call 3 → lantern ↓; open at 22 → P1 alights (ride 13).
    ///  t=26  closed → down. Passes 7 again at t=30. Reaches 3 at t=38; open at 39 → P2 boards (wait 38), presses 1.
    ///  t=43  closed → down. Reaches 1 at t=47; open at 48 → P2 alights (ride 9). Next goal 7, lantern ↑.
    ///  t=52  closed → up. Reaches 7 at t=64; open at 65 → P3 boards (wait 63), presses 8.
    ///  t=69  closed → up. Reaches 8 at t=71; open at 72 → P3 alights (ride 7). No work: lantern off.
    ///  t=76  doors closed, idle, everyone delivered → run ends.
    ///
    ///  Floors travelled 4+4+6+2+6+1 = 23; stops at 5, 9, 3, 1, 7, 8 = 6.
    /// </summary>
    [Fact]
    public void Basic_scenario_matches_hand_computed_trace()
    {
        var result = ScenarioRunner.Run(Repo.Load("v1-basic.yaml"));

        Assert.Equal(
            [(1, 9_000L), (2, 39_000L), (3, 65_000L)],
            result.Events.OfType<PassengerBoarded>().Select(e => (e.PassengerId, e.At.Ms)));
        Assert.Equal(
            [(1, 22_000L, 9), (2, 48_000L, 1), (3, 72_000L, 8)],
            result.Events.OfType<PassengerAlighted>().Select(e => (e.PassengerId, e.At.Ms, e.Floor)));
        Assert.Equal([5, 9, 3, 1, 7, 8], result.Events.OfType<DoorsOpened>().Select(e => e.Floor));
        Assert.Equal(76_000, result.EndedAt.Ms);

        var report = ReportBuilder.Build(result.Events);
        Assert.Equal((3, 3, 0), (report.Passengers, report.Delivered, report.Unserved));
        Assert.Equal((110.0 / 3, 63.0, 63.0), (report.Wait.Avg!.Value, report.Wait.P95!.Value, report.Wait.Max!.Value));
        Assert.Equal((29.0 / 3, 13.0), (report.Ride.Avg!.Value, report.Ride.Max!.Value));
        Assert.Equal((23, 6, 76.0), (report.FloorsTravelled, report.Stops, report.SimTimeS));
    }

    [Theory]
    [InlineData("v1-basic.yaml")]
    [InlineData("v1-poisson.yaml")]
    [InlineData("lunch.yaml")] // the default system: LOOK + Cost, 4 elevators
    [InlineData("chaos.yaml")]
    public void Same_seed_gives_byte_identical_event_log(string scenario)
    {
        var a = Repo.ToJsonl(ScenarioRunner.Run(Repo.Load(scenario)));
        var b = Repo.ToJsonl(ScenarioRunner.Run(Repo.Load(scenario)));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("v1-basic.yaml")]
    [InlineData("v1-poisson.yaml")]
    [InlineData("lunch.yaml")]
    [InlineData("chaos.yaml")]
    public void Event_log_does_not_depend_on_tick_size(string scenario)
    {
        var reference = Repo.ToJsonl(ScenarioRunner.Run(Repo.Load(scenario), tickMs: 100));
        foreach (var tick in new long[] { 1, 50, 250, 1_000, 60_000 })
            Assert.Equal(reference, Repo.ToJsonl(ScenarioRunner.Run(Repo.Load(scenario), tickMs: tick)));
    }

    [Fact]
    public void Different_seed_gives_different_traffic()
    {
        var scenario = Repo.Load("v1-poisson.yaml");
        var a = Repo.ToJsonl(ScenarioRunner.Run(scenario));
        var b = Repo.ToJsonl(ScenarioRunner.Run(scenario with { Seed = scenario.Seed + 1 }));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Poisson_scenario_delivers_everyone()
    {
        var result = ScenarioRunner.Run(Repo.Load("v1-poisson.yaml"));
        Assert.True(result.Delivered > 30, $"only {result.Delivered} passengers generated");
        Assert.Equal(0, result.Unserved);
    }

    [Fact]
    public void Run_stops_at_max_sim_time_and_reports_unserved()
    {
        var scenario = Repo.Load("v1-basic.yaml") with { MaxSimTime = new Domain.SimTime(20_000) };
        var result = ScenarioRunner.Run(scenario);

        Assert.Equal(20_000, result.EndedAt.Ms); // P1 is riding (boarded at 9 s), P2 and P3 still waiting
        Assert.Equal((0, 3), (result.Delivered, result.Unserved));
        Assert.Equal(3, ReportBuilder.Build(result.Events).Unserved);
    }

    /// <summary>
    /// Golden logs of the default system (LOOK + Cost, 4 elevators) and of the chaos schedule (ADR-015): any change in
    /// what the simulator does, or in the order it logs it, changes the hash. When a change is intended, measure it
    /// (docs/results), explain it in an ADR, then update the hash here.
    /// </summary>
    [Theory]
    [InlineData("lunch.yaml", "54e7f3b1733d60170b5db50d67e4468c034a3bdcb7c58c2906c3a7d8dd9e7e29")]
    [InlineData("chaos.yaml", "b180d1ede5f6707001a10bfc45e2aa3722b98c2f6c710e27323f71179274c5ea")]
    public void Golden_event_log_hash(string scenario, string sha256)
    {
        var jsonl = Repo.ToJsonl(ScenarioRunner.Run(Repo.Load(scenario)));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(jsonl)));
        Assert.Equal(sha256, hash);
    }
}
