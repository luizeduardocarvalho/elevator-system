using Elevator.Domain.Events;

namespace Elevator.Simulation.Tests;

public class LookScenarioTests
{
    /// <summary>
    /// v1-basic under LOOK, traced by hand (compare FIFO: waits 9/38/63, ends at 76 s):
    ///  t=0  P1 5↑ → up. t=1 P2 3↓ and t=2 P3 7↑ assigned. Passes 3 at t=4 (down call, wrong direction).
    ///  t=8  stops at 5, open 9 → P1 boards (wait 9), presses 9. Closed 13; lowest up stop ahead = 7 (P3's call).
    ///  t=17 stops at 7, open 18 → P3 boards (wait 16), presses 8. Closed 22 → 8 at 24, open 25 → P3 off (ride 7).
    ///  t=29 closed → 9 at 31; nothing above, reverse: lantern ↓; open 32 → P1 off (ride 23).
    ///  t=36 closed → down 9→3 (12 s) → 48, open 49 → P2 boards (wait 48), presses 1.
    ///  t=53 closed → 1 at 57, open 58 → P2 off (ride 9). Closed 62 → done.
    /// LOOK cuts the mean wait (36.7 → 24.3 s) and the max (63 → 48 s), at the cost of P1's ride (13 → 23 s).
    /// </summary>
    [Fact]
    public void Basic_scenario_under_look_matches_hand_trace()
    {
        var result = ScenarioRunner.Run(Repo.Load("v1-basic.yaml"), new Strategy("look", "nearest"));

        Assert.Equal(
            [(1, 9_000L), (3, 18_000L), (2, 49_000L)],
            result.Events.OfType<PassengerBoarded>().Select(e => (e.PassengerId, e.At.Ms)));
        Assert.Equal(
            [(3, 25_000L), (1, 32_000L), (2, 58_000L)],
            result.Events.OfType<PassengerAlighted>().Select(e => (e.PassengerId, e.At.Ms)));
        Assert.Equal(62_000, result.EndedAt.Ms);
    }
}
