using Elevator.Domain.Events;

namespace Elevator.Simulation.Tests;

public class LookScenarioTests
{
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
