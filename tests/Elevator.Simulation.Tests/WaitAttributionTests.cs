using Elevator.Metrics;
using static Elevator.Metrics.WaitBucket;

namespace Elevator.Simulation.Tests;

public class WaitAttributionTests
{
    /// <summary>
    /// v1-basic attributed by hand (2 s/floor; stops are 1 s opening + 3 s open + 1 s closing = 5 s).
    /// Every passenger is accepted the instant they call. The elevator's journey: up 1→5 (0–8), stop at 5 (8–13),
    /// up 5→9 (13–21), stop at 9 (21–26), down 9→3 (26–38), stop at 3 (38–43), down 3→1 (43–47), stop at 1 (47–52),
    /// up 1→7 (52–64), stop at 7 (64–69).
    ///
    /// P1 (5↑, called 0, boards 9): moving toward 0–8, doors opening at its floor 8–9 → toward 9.
    /// P2 (3↓, called 1, boards 39): toward 1–4 (floors 1→3) = 3; away 4–8 = 4; stop at 5 = serving 5;
    ///   away 13–21 = 8; stop at 9 = serving 5; toward 26–38 = 12; opening at 3 38–39 = 1.
    ///   toward 16, away 12, serving 10 → 38.
    /// P3 (7↑, called 2, boards 65): toward 2–8 = 6; serving 8–13 = 5; toward 13–17 = 4; away 17–21 = 4 (passed 7);
    ///   serving 21–26 = 5; toward 26–30 = 4; away 30–38 = 8 (passed 7 again); serving 38–43 = 5; away 43–47 = 4;
    ///   serving 47–52 = 5; toward 52–64 = 12; opening 64–65 = 1.
    ///   toward 27, away 16, serving 20 → 63.
    /// </summary>
    [Fact]
    public void Basic_scenario_wait_is_attributed_exactly()
    {
        var records = PassengerLifecycle.Analyze(ScenarioRunner.Run(Repo.Load("v1-basic.yaml")).Events);

        Expect(records[0], (TravelToward, 9));
        Expect(records[1], (TravelToward, 16), (TravelAway, 12), (ServingOthers, 10));
        Expect(records[2], (TravelToward, 27), (TravelAway, 16), (ServingOthers, 20));
        Assert.All(records, r => Assert.Equal((0, 0, 1), (r.LeftBehind, r.Rejections, r.Assignments)));
        Assert.Equal((65_000L, 64_000L), (records[2].BoardedAt!.Value.Ms, records[2].ElevatorArrivedAt!.Value.Ms));
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
