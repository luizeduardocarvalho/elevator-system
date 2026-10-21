using Elevator.Domain;

namespace Elevator.Simulation.Tests;

public class ScenarioLoaderTests
{
    private static IReadOnlyList<string> Errors(string yaml) => ScenarioLoader.Load(yaml).Errors;

    [Fact]
    public void Minimal_scenario_gets_defaults()
    {
        var s = ScenarioLoader.Load("passengers:\n  - { at: 1.5, from: 1, to: 4 }").Scenario!;
        Assert.Equal((10, "fifo", 3_600_000L), (s.Floors, s.Scheduling, s.MaxSimTime.Ms));
        Assert.Equal(ElevatorTiming.Default, s.Timing);
        Assert.Equal(new PassengerSpec(new SimTime(1_500), 1, 4), Assert.Single(s.Passengers));
    }

    [Theory]
    [InlineData("{ at: 0, from: 0, to: 4 }", "passengers[0]: pickup: floor 0 is outside 1..10")]
    [InlineData("{ at: 0, from: 3, to: 11 }", "passengers[0]: destination: floor 11 is outside 1..10")]
    [InlineData("{ at: 0, from: 4, to: 4 }", "passengers[0]: pickup and destination are both floor 4")]
    [InlineData("{ at: 0, from: 2, to: 6, direction: down }", "passengers[0]: direction Down is inconsistent with 2→6")]
    [InlineData("{ at: 0, from: 2, to: 6, direction: sideways }", "passengers[0]: direction must be 'up' or 'down' (got 'sideways')")]
    [InlineData("{ at: -1, from: 2, to: 6 }", "passengers[0]: 'at' must be >= 0")]
    [InlineData("{ from: 2, to: 6 }", "passengers[0]: 'at', 'from' and 'to' are required")]
    public void Invalid_passenger_is_reported_with_its_index(string passenger, string expected) =>
        Assert.Contains(expected, Errors($"passengers:\n  - {passenger}"));

    [Fact]
    public void All_errors_are_reported_at_once() =>
        Assert.Equal(3, Errors("scheduling: magic\npassengers:\n  - { at: 0, from: 1, to: 1 }\n  - { at: 0, from: 1, to: 99 }").Count);

    [Fact]
    public void Unknown_key_is_an_error_not_silently_ignored() =>
        Assert.Contains("invalid YAML at line 1", Assert.Single(Errors("flors: 10")));

    [Fact]
    public void Malformed_yaml_is_reported() =>
        Assert.StartsWith("invalid YAML", Assert.Single(Errors("passengers: [ { at: 0 ")));

    [Fact]
    public void Missing_file_is_reported() =>
        Assert.StartsWith("scenario file not found", Assert.Single(ScenarioLoader.LoadFile("nope.yaml").Errors));
}
