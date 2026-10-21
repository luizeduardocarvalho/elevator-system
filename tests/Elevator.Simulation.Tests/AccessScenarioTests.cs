using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Metrics;

namespace Elevator.Simulation.Tests;

/// <summary>Eligibility before cost (ADR-012), end to end through the sim engine.</summary>
public class AccessScenarioTests
{
    private const string Yaml = """
        name: access
        building:
          floors: 20
          types:
            local: {}
            express: { floorTravelMs: 1000, servedFloors: [1, 12-20] }
            freight: { capacity: 20 }
          fleet: [local, local, express, freight]
        access:
          restrictedFloors: { 20: 2 }
          vip: { priority: true, dedicated: [1] }
        passengers:
          - { at: 0,  from: 1,  to: 20 }                       # 1: no access to 20 -> refused
          - { at: 0,  from: 1,  to: 20, accessLevel: 2 }       # 2: allowed
          - { at: 1,  from: 1,  to: 5 }                        # 3: express cannot take it
          - { at: 2,  from: 3,  to: 9, load: 15 }              # 4: only the freight can carry it
          - { at: 3,  from: 1,  to: 14, vip: true }            # 5: VIP: any elevator, E1 included
          - { at: 4,  from: 2,  to: 7 }                        # 6
          - { at: 5,  from: 15, to: 1 }                        # 7
          - { at: 6,  from: 1,  to: 9, load: 30 }              # 8: fits nowhere -> refused
        """;

    private static Scenario Load() => ScenarioLoader.Load(Yaml).Scenario!;

    [Fact]
    public void Access_config_is_loaded()
    {
        var a = Load().Access;
        Assert.Equal((2, true), (a.RestrictedFloors[20], a.VipPriority));
        Assert.Equal([1], a.VipElevators);
    }

    [Theory]
    [InlineData("access: { restrictedFloors: { 30: 2 } }", "access.restrictedFloors: floor 30 is outside 1..10")]
    [InlineData("access: { restrictedFloors: { 3: 0 } }", "access.restrictedFloors: floor 3 needs a level >= 1")]
    [InlineData("access: { vip: { dedicated: [2] } }", "access.vip.dedicated: elevator 2 does not exist (0..0)")]
    public void Invalid_access_config_is_reported(string yaml, string expected) =>
        Assert.Contains(ScenarioLoader.Load(yaml).Errors, e => e.StartsWith(expected, StringComparison.Ordinal));

    [Fact]
    public void Every_rule_holds_and_refused_requests_are_counted_not_lost()
    {
        var result = SimEngine.Run(Load());
        var events = result.Events;

        // Refused by policy: logged with no elevator, never boarded, and the run still completes.
        Assert.Equal([1, 8], events.OfType<AccessDenied>().Where(a => a.ElevatorId is null).Select(a => a.RequestId));
        Assert.Equal("floor-restriction", events.OfType<AccessDenied>().Single(a => a is { RequestId: 1, ElevatorId: null }).Policy);
        // Different elevators refuse for different reasons (the express also does not serve 9): all are named.
        Assert.Equal(("capacity+served-floors", "no elevator may serve it"),
            events.OfType<AccessDenied>().Where(a => a is { RequestId: 8, ElevatorId: null }).Select(a => (a.Policy, a.Reason)).Single());
        Assert.Equal(6, result.Delivered);
        Assert.DoesNotContain(events.OfType<PassengerBoarded>(), b => b.PassengerId is 1 or 8);

        var boardedOn = events.OfType<PassengerBoarded>().ToDictionary(b => b.PassengerId, b => b.ElevatorId);
        Assert.NotEqual(2, boardedOn[3]);         // the express does not serve 5
        Assert.Equal(3, boardedOn[4]);            // only the freight carries 15 units
        Assert.All(boardedOn.Where(kv => kv.Value == 1), kv => Assert.Equal(5, kv.Key)); // E1 carries VIPs only
        Assert.All(events.OfType<DoorsOpened>().Where(d => d.ElevatorId == 2), d => Assert.True(d.Floor is 1 or >= 12));

        var report = ReportBuilder.Build(events);
        Assert.Equal((6, 2, 0), (report.Delivered, report.RefusedByPolicy, report.Unserved));
    }
}
