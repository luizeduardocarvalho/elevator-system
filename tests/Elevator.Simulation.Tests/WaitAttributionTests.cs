using Elevator.Metrics;
using static Elevator.Metrics.WaitBucket;

namespace Elevator.Simulation.Tests;

public class WaitAttributionTests
{
    /// <summary>
    /// v1-basic attributed by hand (LOOK; 2 s/floor; stops are 1 s opening + 3 s open + 1 s closing = 5 s).
    /// Every passenger is accepted the instant they call. The elevator's journey: up 1→5 (0–8), stop at 5 (8–13),
    /// up 5→7 (13–17), stop at 7 (17–22), up 7→8 (22–24), stop at 8 (24–29), up 8→9 (29–31), stop at 9 (31–36),
    /// down 9→3 (36–48), stop at 3 (48–53), down 3→1 (53–57), stop at 1 (57–62).
    ///
    /// P1 (5↑, called 0, boards 9): moving toward 0–8, doors opening at its floor 8–9 → toward 9.
    /// P2 (3↓, called 1, boards 49): toward 1–4 (floors 1→3) = 3; away 4–8 = 4; stop at 5 = serving 5;
    ///   away 13–17 = 4; stop at 7 = serving 5; away 22–24 = 2; stop at 8 = serving 5; away 29–31 = 2;
    ///   stop at 9 = serving 5; toward 36–48 = 12; opening at 3 48–49 = 1.
    ///   toward 16, away 12, serving 20 → 48.
    /// P3 (7↑, called 2, boards 18): toward 2–8 = 6; stop at 5 = serving 5; toward 13–17 = 4; opening at 7 17–18 = 1.
    ///   toward 11, serving 5 → 16.
    /// </summary>
    [Fact]
    public void Basic_scenario_wait_is_attributed_exactly()
    {
        var records = PassengerLifecycle.Analyze(ScenarioRunner.Run(Repo.Load("v1-basic.yaml")).Events);

        Expect(records[0], (TravelToward, 9));
        Expect(records[1], (TravelToward, 16), (TravelAway, 12), (ServingOthers, 20));
        Expect(records[2], (TravelToward, 11), (ServingOthers, 5));
        Assert.All(records, r => Assert.Equal((0, 0, 1), (r.LeftBehind, r.Rejections, r.Assignments)));
        Assert.Equal((18_000L, 17_000L), (records[2].BoardedAt!.Value.Ms, records[2].ElevatorArrivedAt!.Value.Ms));
    }

    private static void Expect(PassengerRecord r, params (WaitBucket Bucket, double Seconds)[] expected)
    {
        foreach (var bucket in Enum.GetValues<WaitBucket>())
        {
            var want = expected.FirstOrDefault(e => e.Bucket == bucket).Seconds;
            Assert.True(Math.Abs(want - r.WaitBuckets[bucket]) < 1e-9, $"P{r.Id} {bucket}: expected {want}, got {r.WaitBuckets[bucket]}");
        }
        Assert.Equal(r.WaitS!.Value, r.WaitBuckets.Values.Sum(), 9);
    }
}
