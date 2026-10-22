using Elevator.Metrics;
using static Elevator.Metrics.WaitBucket;

namespace Elevator.Simulation.Tests;

public class WaitAttributionTests
{
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
