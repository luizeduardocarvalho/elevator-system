using Elevator.Domain;
using Elevator.Domain.Control;

namespace Elevator.Simulation.Tests;

public class ScenarioLoaderTests
{
    private static IReadOnlyList<string> Errors(string yaml) => ScenarioLoader.Load(yaml).Errors;

    [Fact]
    public void Minimal_scenario_gets_defaults()
    {
        var s = ScenarioLoader.Load("passengers:\n  - { at: 1.5, from: 1, to: 4 }").Scenario!;
        Assert.Equal((10, "cost", 3_600_000L), (s.Floors, s.Dispatch, s.MaxSimTime.Ms));
        Assert.Equal(ElevatorTiming.Default, s.Timing);
        Assert.Equal(new PassengerSpec(new Instant(1_500), 1, 4), Assert.Single(s.Passengers));
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
        Assert.Equal(3, Errors("dispatch: magic\npassengers:\n  - { at: 0, from: 1, to: 1 }\n  - { at: 0, from: 1, to: 99 }").Count);

    [Fact]
    public void Unknown_key_is_an_error_not_silently_ignored() =>
        Assert.Contains("invalid YAML at line 1", Assert.Single(Errors("flors: 10")));

    [Fact]
    public void Malformed_yaml_is_reported() =>
        Assert.StartsWith("invalid YAML", Assert.Single(Errors("passengers: [ { at: 0 ")));

    [Fact]
    public void Missing_file_is_reported() =>
        Assert.StartsWith("scenario file not found", Assert.Single(ScenarioLoader.LoadFile("nope.yaml").Errors));

    [Fact]
    public void Building_dispatch_latency_and_profile_are_loaded()
    {
        var s = ScenarioLoader.Load("""
            building: { floors: 20, elevators: 4, capacity: 12, bypassThreshold: 0.75 }
            dispatch: nearest
            latency: { snapshotMs: 500, dispatchMs: 100 }
            traffic:
              profile:
                lobby: 1
                mix: { fromLobby: 0.85, toLobby: 0.05, interfloor: 0.10 }
                rate: [ { atS: 0, perMinute: 4 }, { atS: 600, perMinute: 12 } ]
            """).Scenario!;
        Assert.Equal((20, 4, 12, 0.75, "nearest"), (s.Floors, s.Elevators, s.Capacity, s.BypassThreshold, s.Dispatch));
        Assert.Equal(new MessageLatency(500, 100), s.Latency);
        Assert.Equal(2, s.Profile!.Rate.Count);
    }

    [Theory]
    [InlineData("building: { elevators: 0 }", "building.elevators must be at least 1")]
    [InlineData("building: { capacity: 0 }", "building.capacity must be at least 1")]
    [InlineData("building: { bypassThreshold: 1.5 }", "building.bypassThreshold must be in (0, 1]")]
    [InlineData("dispatch: psychic", "unknown dispatch 'psychic'")]
    [InlineData("floors: 10\nbuilding: { floors: 12 }", "give 'floors' or 'building.floors', not both")]
    [InlineData("traffic: { profile: { mix: { fromLobby: 1 }, rate: [ { atS: 0, perMinute: 1 } ] } }", "traffic.profile.rate needs at least two points")]
    public void Invalid_building_settings_are_reported(string yaml, string expected) =>
        Assert.Contains(Errors(yaml.Replace("\\n", "\n")), e => e.StartsWith(expected, StringComparison.Ordinal));
}
