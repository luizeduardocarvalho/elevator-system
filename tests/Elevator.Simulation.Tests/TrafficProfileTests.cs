namespace Elevator.Simulation.Tests;

public class TrafficProfileTests
{
    private static readonly TrafficMix AllInterfloor = new(0, 0, 1);

    private static double MeanCount(TrafficProfile profile, Func<PassengerSpec, bool> where, int seeds = 300) =>
        Enumerable.Range(1, seeds).Average(seed => profile.Generate(20, new Random(seed)).Count(where));

    [Fact]
    public void Constant_rate_gives_the_expected_number_of_arrivals()
    {
        // 30/min for 600 s → 300 expected; with 300 seeds the standard error of the mean is √300/√300 = 1.
        var profile = new TrafficProfile([new RatePoint(0, 30), new RatePoint(600, 30)], AllInterfloor);
        Assert.InRange(MeanCount(profile, _ => true), 296, 304);
    }

    [Fact]
    public void Thinning_follows_a_ramp()
    {
        // λ(t) rises linearly from 0 to 60/min (1/s) over 600 s: ∫₀³⁰⁰ t/600 dt = 75 in the first half,
        // ∫₃₀₀⁶⁰⁰ t/600 dt = 225 in the second.
        var profile = new TrafficProfile([new RatePoint(0, 0), new RatePoint(600, 60)], AllInterfloor);
        Assert.InRange(MeanCount(profile, p => p.At.Seconds < 300), 73, 77);
        Assert.InRange(MeanCount(profile, p => p.At.Seconds >= 300), 221, 229);
    }

    [Fact]
    public void Origin_destination_mix_is_respected()
    {
        var profile = new TrafficProfile([new RatePoint(0, 60), new RatePoint(3600, 60)], new TrafficMix(0.85, 0.05, 0.10), Lobby: 1);
        var trips = profile.Generate(20, new Random(7)).ToList();
        double Share(Func<PassengerSpec, bool> f) => trips.Count(f) / (double)trips.Count;

        Assert.InRange(Share(p => p.From == 1), 0.82, 0.88);
        Assert.InRange(Share(p => p.To == 1), 0.03, 0.07);
        Assert.All(trips, p => Assert.NotEqual(p.From, p.To));
        Assert.All(trips, p => Assert.InRange(p.From, 1, 20));
    }

    [Fact]
    public void Same_seed_same_traffic_regardless_of_strategy()
    {
        var scenario = Repo.Load("up-peak.yaml");
        Assert.Equal(ScenarioRunner.BuildPassengers(scenario), ScenarioRunner.BuildPassengers(scenario with { Dispatch = "roundrobin", Scheduling = "fifo" }));
        Assert.NotEqual(ScenarioRunner.BuildPassengers(scenario), ScenarioRunner.BuildPassengers(scenario with { Seed = 2 }));
    }
}
