using Elevator.Domain;
using Elevator.Domain.Events;

namespace Elevator.Simulation.Tests;

/// <summary>Elevator types as configuration (ADR-012): YAML, validation, and their effect on a run.</summary>
public class ElevatorTypeScenarioTests
{
    private const string Fleet = """
        name: typed
        building:
          floors: 20
          capacity: 10
          types:
            local: {}
            express: { floorTravelMs: 1000, servedFloors: [1, 12-20] }
            freight: { floorTravelMs: 3000, doorDwellMs: 8000, capacity: 20 }
          fleet: [local, local, express, freight]
        """;

    [Fact]
    public void Types_default_to_the_scenario_timing_capacity_and_every_floor()
    {
        var s = ScenarioLoader.Load(Fleet).Scenario!;
        Assert.Equal(4, s.Elevators);
        Assert.Equal(["local", "local", "express", "freight"], s.EffectiveFleet.Select(t => t.Name));
        Assert.Equal(new ElevatorType("local", ElevatorTiming.Default, 10), s.EffectiveFleet[0]);
        Assert.Equal(new ElevatorType("express", ElevatorTiming.Default with { FloorTravelMs = 1000 }, 10, FloorMask.Of([1, .. Enumerable.Range(12, 9)])),
            s.EffectiveFleet[2]);
        Assert.Equal((3000, 8000, 20, FloorMask.All), (s.EffectiveFleet[3].Timing.FloorTravelMs, s.EffectiveFleet[3].Timing.DoorDwellMs,
            s.EffectiveFleet[3].Capacity, s.EffectiveFleet[3].ServedFloors));
    }

    [Fact]
    public void Without_types_the_fleet_is_standard_elevators_from_the_scenario()
    {
        var s = Repo.Load("lunch.yaml");
        Assert.Null(s.Fleet);
        Assert.All(s.EffectiveFleet, t => Assert.Equal(ElevatorType.Standard(s.Timing, s.Capacity), t));
        Assert.Equal(4, s.EffectiveFleet.Count);
    }

    [Theory]
    [InlineData("building: { elevators: 3, types: { a: {} }, fleet: [a, a] }", "building.elevators (3) does not match building.fleet (2 entries)")]
    [InlineData("building: { types: { a: {} }, fleet: [a, b] }", "building.fleet[1]: unknown type 'b'")]
    [InlineData("building: { types: { a: {} } }", "building.types is given but building.fleet is missing")]
    [InlineData("building: { floors: 20, types: { a: { servedFloors: [1, 30] } }, fleet: [a] }", "building.types.a.servedFloors: '30': floor 30 is outside 1..20")]
    [InlineData("building: { types: { a: { capacity: 0 } }, fleet: [a] }", "building.types.a: capacity must be at least 1")]
    [InlineData("building: { types: { a: { doorDwellMs: 0 } }, fleet: [a] }", "building.types.a: doorDwellMs must be > 0")]
    [InlineData("passengers: [{ at: 1, from: 1, to: 3, load: 0 }]", "passengers[0]: load must be > 0")]
    [InlineData("passengers: [{ at: 1, from: 1, to: 3, accessLevel: -1 }]", "passengers[0]: 'accessLevel' must be >= 0")]
    public void Invalid_type_config_is_reported(string yaml, string expected) =>
        Assert.Contains(ScenarioLoader.Load(yaml).Errors, e => e.StartsWith(expected, StringComparison.Ordinal));

    [Fact]
    public void Scripted_passengers_carry_vip_access_level_and_load()
    {
        var s = ScenarioLoader.Load("""
            passengers:
              - { at: 1, from: 1, to: 3, vip: true, accessLevel: 2, load: 4 }
              - { at: 2, from: 3, to: 1 }
            """).Scenario!;
        var requests = ScenarioRunner.BuildPassengers(s).Select(p => p.ToRequest()).ToList();
        Assert.Equal((true, 2, 4), (requests[0].Vip, requests[0].AccessLevel, requests[0].Load));
        Assert.Equal((false, 0, 1), (requests[1].Vip, requests[1].AccessLevel, requests[1].Load));
    }

    [Fact]
    public void Each_elevator_runs_with_its_type()
    {
        // Trips between the lobby and the top floors, which every type serves.
        var s = ScenarioLoader.Load(Fleet + """

            passengers:
              - { at: 0, from: 1, to: 20 }
              - { at: 0, from: 1, to: 15 }
              - { at: 1, from: 1, to: 18 }
              - { at: 2, from: 1, to: 12 }
              - { at: 3, from: 20, to: 1 }
            """).Scenario!;
        var engine = SimEngine.Create(s);
        Assert.Equal(["local", "local", "express", "freight"], engine.Elevators.Select(e => e.TypeName));
        Assert.Equal(1, engine.Elevators[2].CurrentFloor);
        var result = SimEngine.Run(s);
        Assert.Equal(5, result.Delivered);

        // The express never opens at a floor it does not serve; the freight takes 3 s per floor.
        Assert.All(result.Events.OfType<DoorsOpened>().Where(d => d.ElevatorId == 2), d => Assert.True(d.Floor is 1 or >= 12));
        var freightFloors = result.Events.OfType<FloorReached>().Where(f => f.ElevatorId == 3).Select(f => f.At.Ms).ToList();
        Assert.All(freightFloors.Zip(freightFloors.Skip(1)), p => Assert.True(p.Second - p.First >= 3_000));
    }
}
