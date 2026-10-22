using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Policy.Access;

namespace Elevator.Simulation.Tests;

public class StrandedRiderTests
{
    /// <summary>The case FsCheck found (HardStageProperties, replay (14274801865919652370,16715946969894330217)).</summary>
    private static Scenario Found()
    {
        var low = new ElevatorType("low", ElevatorTiming.Default, 4, FloorMask.Of(Enumerable.Range(1, 7)));
        var freight = new ElevatorType("freight", new ElevatorTiming(3_000, 2_000, 6_000, 2_000), 8);
        PassengerSpec P(int at, int from, int to, int level, int load, bool vip = false) =>
            new(new Instant(at), from, to) { AccessLevel = level, Load = load, Vip = vip };
        FaultCommand F(int at, FaultKind kind, long? duration = null) => new(new Instant(at), 0, kind, duration);
        return new Scenario("stranded", 1, 13, ElevatorTiming.Default, Instant.FromSeconds(200_000),
            [
                P(103713, 1, 12, 2, 2), P(199358, 3, 4, 2, 3), P(118040, 6, 13, 2, 3), P(130918, 11, 13, 2, 1, vip: true),
                P(119107, 10, 6, 0, 1), P(48334, 9, 2, 0, 1), P(30502, 10, 3, 0, 1), P(78882, 8, 11, 0, 2),
                P(212895, 12, 13, 2, 1, vip: true), P(224382, 4, 6, 0, 1), P(115733, 8, 9, 0, 1),
            ],
            null, 3, 4, 1.0, "nearest", null, new MessageLatency(0, 0),
            [
                F(77822, FaultKind.MaintenanceOn), F(157797, FaultKind.Stuck, 42707), F(177232, FaultKind.MaintenanceOff),
                F(211940, FaultKind.Stuck, 70090), F(231470, FaultKind.EmergencyStop), F(264295, FaultKind.EmergencyClear),
            ])
        {
            Fleet = [low, freight, low],
            Access = new AccessConfig(new Dictionary<int, int> { [4] = 1 }, [], false),
        };
    }

    /// <summary>
    /// E0 starts opening its doors at floor 4 for passenger 9, gets stuck, is emergency-stopped (the doors reverse) and
    /// cleared while they are still closing. Before the fix it went idle with passenger 9 on board, forever.
    /// </summary>
    [Fact]
    public void A_rider_whose_stop_was_cut_short_by_an_emergency_is_still_delivered()
    {
        var result = SimEngine.Run(Found());

        var refused = result.Events.OfType<AccessDenied>().Where(a => a.ElevatorId is null).Select(a => a.RequestId).ToList();
        Assert.Equal([11], refused); // floor 4 needs access level 1; passenger 11 has none
        Assert.Equal(refused.Count, result.Unserved);
        Assert.Contains(result.Events, e => e is PassengerAlighted { PassengerId: 9, Floor: 4 });
    }
}
