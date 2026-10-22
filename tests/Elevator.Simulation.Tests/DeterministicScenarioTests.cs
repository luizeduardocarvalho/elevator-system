using Elevator.Domain.Events;
using Elevator.Metrics;

namespace Elevator.Simulation.Tests;

public class DeterministicScenarioTests
{
    [Fact]
    public void Basic_scenario_matches_hand_computed_trace()
    {
        var result = ScenarioRunner.Run(Repo.Load("v1-basic.yaml"));

        Assert.Equal(
            [(1, 9_000L), (3, 18_000L), (2, 49_000L)],
            result.Events.OfType<PassengerBoarded>().Select(e => (e.PassengerId, e.At.Ms)));
        Assert.Equal(
            [(3, 25_000L, 8), (1, 32_000L, 9), (2, 58_000L, 1)],
            result.Events.OfType<PassengerAlighted>().Select(e => (e.PassengerId, e.At.Ms, e.Floor)));
        Assert.Equal([5, 7, 8, 9, 3, 1], result.Events.OfType<DoorsOpened>().Select(e => e.Floor));
        Assert.Equal(62_000, result.EndedAt.Ms);

        var report = ReportBuilder.Build(result.Events);
        Assert.Equal((3, 3, 0), (report.Passengers, report.Delivered, report.Unserved));
        Assert.Equal((73.0 / 3, 48.0), (report.Wait.Avg!.Value, report.Wait.Max!.Value));
        Assert.Equal((39.0 / 3, 23.0), (report.Ride.Avg!.Value, report.Ride.Max!.Value));
        Assert.Equal((16, 6, 62.0), (report.FloorsTravelled, report.Stops, report.SimTimeS));
    }

    [Theory]
    [InlineData("v1-basic.yaml")]
    [InlineData("v1-poisson.yaml")]
    [InlineData("lunch.yaml")]
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

        Assert.Equal(20_000, result.EndedAt.Ms);
        Assert.Equal((0, 3), (result.Delivered, result.Unserved));
        Assert.Equal(3, ReportBuilder.Build(result.Events).Unserved);
    }

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
