using Elevator.Domain;

namespace Elevator.Simulation;

/// <summary>
/// Arrival rate at a point in time; the rate is linear between points. An optional <paramref name="Mix"/> applies from
/// this point until the next point that has one (a step, not interpolated): a working day switches from up-peak to
/// interfloor to down-peak traffic this way.
/// </summary>
public sealed record RatePoint(double AtS, double PerMinute, TrafficMix? Mix = null);

/// <summary>Share of trips that start at the lobby, end at the lobby, or go between two upper floors.</summary>
public sealed record TrafficMix(double FromLobby, double ToLobby, double Interfloor)
{
    public double Total => FromLobby + ToLobby + Interfloor;
}

/// <summary>
/// Time-varying passenger traffic: a non-homogeneous Poisson process with a piecewise-linear rate λ(t) and an
/// origin/destination mix. <paramref name="Mix"/>
/// is the default; a rate point's own mix overrides it from that point on.
/// </summary>
public sealed record TrafficProfile(IReadOnlyList<RatePoint> Rate, TrafficMix Mix, int Lobby = Floors.Lowest)
{
    /// <summary>The mix in effect at <paramref name="t"/>: that of the last rate point at or before t that has one, else the default.</summary>
    public TrafficMix MixAt(double t)
    {
        var mix = Mix;
        foreach (var p in Rate)
        {
            if (p.AtS > t) break;
            if (p.Mix is { } m) mix = m;
        }
        return mix;
    }

    public double RatePerSecondAt(double t)
    {
        if (t <= Rate[0].AtS) return Rate[0].PerMinute / 60;
        for (var i = 1; i < Rate.Count; i++)
        {
            if (t > Rate[i].AtS) continue;
            var (a, b) = (Rate[i - 1], Rate[i]);
            var f = b.AtS == a.AtS ? 1 : (t - a.AtS) / (b.AtS - a.AtS);
            return (a.PerMinute + f * (b.PerMinute - a.PerMinute)) / 60;
        }
        return Rate[^1].PerMinute / 60;
    }

    /// <summary>
    /// Every draw comes from
    /// <paramref name="rng"/>, so the seed alone fixes the passengers (common random numbers across strategies).
    /// </summary>
    public IEnumerable<PassengerSpec> Generate(int floors, Random rng)
    {
        var lambdaMax = Rate.Max(p => p.PerMinute) / 60;
        if (lambdaMax <= 0) yield break;

        var t = Rate[0].AtS;
        var end = Rate[^1].AtS;
        while (true)
        {
            t += -Math.Log(1 - rng.NextDouble()) / lambdaMax;
            if (t >= end) yield break;
            if (rng.NextDouble() * lambdaMax >= RatePerSecondAt(t)) continue;

            var (from, to) = Trip(MixAt(t), floors, rng);
            yield return new PassengerSpec(SimTime.FromSeconds(t), from, to);
        }
    }

    private (int From, int To) Trip(TrafficMix mix, int floors, Random rng)
    {
        var u = rng.NextDouble() * mix.Total;
        if (u < mix.FromLobby) return (Lobby, UpperFloor(floors, rng, exclude: Lobby));
        if (u < mix.FromLobby + mix.ToLobby) return (UpperFloor(floors, rng, exclude: Lobby), Lobby);

        var from = UpperFloor(floors, rng, exclude: Lobby);
        int to;
        do to = UpperFloor(floors, rng, exclude: Lobby); while (to == from);
        return (from, to);
    }

    private static int UpperFloor(int floors, Random rng, int exclude)
    {
        int f;
        do f = rng.Next(Floors.Lowest, floors + 1); while (f == exclude);
        return f;
    }

    public IReadOnlyList<string> Validate(int floors)
    {
        var errors = new List<string>();
        if (Rate.Count < 2) errors.Add("traffic.profile.rate needs at least two points");
        for (var i = 1; i < Rate.Count; i++)
            if (Rate[i].AtS < Rate[i - 1].AtS) errors.Add("traffic.profile.rate points must be in time order");
        if (Rate.Any(p => p.PerMinute < 0 || p.AtS < 0)) errors.Add("traffic.profile.rate values must be >= 0");
        CheckMix(Mix, "traffic.profile.mix");
        for (var i = 0; i < Rate.Count; i++)
            if (Rate[i].Mix is { } m) CheckMix(m, $"traffic.profile.rate[{i}].mix");
        if (Floors.Check(Lobby, floors) is { } e) errors.Add($"traffic.profile.lobby: {e}");
        return errors;

        void CheckMix(TrafficMix mix, string where)
        {
            if (mix.FromLobby < 0 || mix.ToLobby < 0 || mix.Interfloor < 0 || mix.Total <= 0) errors.Add($"{where} shares must be >= 0 and not all 0");
            if (mix.Interfloor > 0 && floors < 3) errors.Add($"{where}: interfloor traffic needs at least 3 floors");
        }
    }
}
