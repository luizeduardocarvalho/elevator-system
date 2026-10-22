namespace Elevator.Simulation.Tests;

public class TrafficProfileTests
{
    private static readonly TrafficMix AllInterfloor = new(0, 0, 1);

    private static double MeanCount(TrafficProfile profile, Func<PassengerSpec, bool> where, int seeds = 300) =>
        Enumerable.Range(1, seeds).Average(seed => profile.Generate(20, new Random(seed)).Count(where));

    [Fact]
    public void Constant_rate_gives_the_expected_number_of_arrivals()
    {
        var profile = new TrafficProfile([new RatePoint(0, 30), new RatePoint(600, 30)], AllInterfloor);
        Assert.InRange(MeanCount(profile, _ => true), 296, 304);
    }

    [Fact]
    public void Thinning_follows_a_ramp()
    {
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
        Assert.Equal(ScenarioRunner.BuildPassengers(scenario), ScenarioRunner.BuildPassengers(scenario with { Dispatch = "nearest" }));
        Assert.NotEqual(ScenarioRunner.BuildPassengers(scenario), ScenarioRunner.BuildPassengers(scenario with { Seed = 2 }));
    }

    [Fact]
    public void A_mix_on_a_rate_point_applies_from_that_point_until_the_next_one()
    {
        var morning = new TrafficMix(1, 0, 0);
        var evening = new TrafficMix(0, 1, 0);
        var profile = new TrafficProfile(
            [new RatePoint(0, 60, morning), new RatePoint(600, 60), new RatePoint(1200, 60, evening), new RatePoint(1800, 60)],
            AllInterfloor, Lobby: 1);

        Assert.Equal(morning, profile.MixAt(0));
        Assert.Equal(morning, profile.MixAt(1199.9));
        Assert.Equal(evening, profile.MixAt(1200));
        Assert.Equal(evening, profile.MixAt(5000));

        var trips = profile.Generate(20, new Random(3)).ToList();
        Assert.All(trips.Where(p => p.At.Seconds < 1200), p => Assert.Equal(1, p.From));
        Assert.All(trips.Where(p => p.At.Seconds >= 1200), p => Assert.Equal(1, p.To));
        Assert.Contains(trips, p => p.At.Seconds >= 1200);
    }

    [Fact]
    public void Without_per_point_mixes_the_profile_mix_applies_everywhere()
    {
        var mix = new TrafficMix(0.85, 0.05, 0.10);
        var profile = new TrafficProfile([new RatePoint(0, 60), new RatePoint(600, 60)], mix);
        Assert.Equal(mix, profile.MixAt(0));
        Assert.Equal(mix, profile.MixAt(10_000));
    }

    [Fact]
    public void A_per_point_mix_equal_to_the_default_draws_the_same_passengers()
    {
        var mix = new TrafficMix(0.4, 0.4, 0.2);
        var plain = new TrafficProfile([new RatePoint(0, 4), new RatePoint(900, 12), new RatePoint(1800, 4)], mix, Lobby: 1);
        var stepped = new TrafficProfile([new RatePoint(0, 4, mix), new RatePoint(900, 12, mix), new RatePoint(1800, 4)], mix, Lobby: 1);
        Assert.Equal(plain.Generate(20, new Random(11)), stepped.Generate(20, new Random(11)));
    }

    [Fact]
    public void Loader_reads_per_point_mixes_and_the_clock_start()
    {
        var s = ScenarioLoader.Load("""
            name: day-test
            floors: 10
            clockStart: "07:30"
            traffic:
              profile:
                mix: { fromLobby: 0.3, toLobby: 0.3, interfloor: 0.4 }
                rate:
                  - { atS: 0,   perMinute: 6, mix: { fromLobby: 1, toLobby: 0, interfloor: 0 } }
                  - { atS: 600, perMinute: 6 }
            """).Scenario!;
        Assert.Equal(new TimeSpan(7, 30, 0), s.ClockStart);
        Assert.Equal(new TrafficMix(1, 0, 0), s.Profile!.Rate[0].Mix);
        Assert.Null(s.Profile.Rate[1].Mix);
        Assert.Null(Repo.Load("lunch.yaml").ClockStart);
    }

    [Theory]
    [InlineData("clockStart: \"25:00\"", "clockStart")]
    [InlineData("clockStart: \"7am\"", "clockStart")]
    public void Loader_rejects_a_bad_clock_start(string line, string expected)
    {
        var errors = ScenarioLoader.Load($"name: x\nfloors: 10\n{line}\n").Errors;
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Loader_rejects_a_bad_per_point_mix()
    {
        var errors = ScenarioLoader.Load("""
            name: x
            floors: 10
            traffic:
              profile:
                mix: { fromLobby: 1 }
                rate:
                  - { atS: 0, perMinute: 6, mix: { fromLobby: -1, toLobby: 0, interfloor: 0 } }
                  - { atS: 600, perMinute: 6 }
            """).Errors;
        Assert.Contains(errors, e => e.Contains("rate[0].mix", StringComparison.Ordinal));
    }

    [Fact]
    public void The_working_day_has_its_three_peaks_in_the_right_directions()
    {
        var day = Repo.Load("day.yaml");
        Assert.Equal(new TimeSpan(7, 0, 0), day.ClockStart);
        var trips = Enumerable.Range(1, 5).SelectMany(seed => ScenarioRunner.BuildPassengers(day with { Seed = seed })).ToList();
        static bool Between(Passenger p, double fromH, double toH) => p.ArrivesAt.Seconds >= (fromH - 7) * 3600 && p.ArrivesAt.Seconds < (toH - 7) * 3600;
        double FromLobby(double a, double b) => Share(trips.Where(p => Between(p, a, b)), p => p.From == 1);
        double ToLobby(double a, double b) => Share(trips.Where(p => Between(p, a, b)), p => p.To == 1);
        static double Share(IEnumerable<Passenger> ps, Func<Passenger, bool> f) { var l = ps.ToList(); return l.Count(f) / (double)l.Count; }

        Assert.True(FromLobby(8, 9.5) > 0.75, "morning: mostly from the lobby");
        Assert.True(ToLobby(17, 18.5) > 0.75, "evening: mostly to the lobby");
        Assert.True(ToLobby(12, 12.5) > 0.6, "first lunch hump: out to the lobby");
        Assert.True(FromLobby(12.5, 13.2) > 0.6, "second lunch hump: back from the lobby");
        double PerHour(double a, double b) => trips.Count(p => Between(p, a, b)) / 5.0 / (b - a);
        Assert.True(PerHour(8.25, 9) > 3 * PerHour(10, 11.5), "the morning peak stands out from mid-morning");
        Assert.True(PerHour(17.1, 18) > 3 * PerHour(14, 16), "the evening peak stands out from the afternoon");
    }
}
