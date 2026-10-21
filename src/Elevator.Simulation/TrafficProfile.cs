using Elevator.Domain;

namespace Elevator.Simulation;

/// <summary>Arrival rate at a point in time; the rate is linear between points.</summary>
public sealed record RatePoint(double AtS, double PerMinute);

/// <summary>Share of trips that start at the lobby, end at the lobby, or go between two upper floors.</summary>
public sealed record TrafficMix(double FromLobby, double ToLobby, double Interfloor)
{
    public double Total => FromLobby + ToLobby + Interfloor;
}

/// <summary>
/// Time-varying passenger traffic: a non-homogeneous Poisson process with a piecewise-linear rate λ(t) and an
/// origin/destination mix. Up-peak is mostly from the lobby, down-peak mostly to it, lunch is both.
/// </summary>
public sealed record TrafficProfile(IReadOnlyList<RatePoint> Rate, TrafficMix Mix, int Lobby = Floors.Lowest)
{
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
    /// Thinning (Lewis–Shedler): generate candidate arrivals from a homogeneous process at the peak rate λmax
    /// (exponential gaps, as in <see cref="PoissonTraffic"/>), and keep each candidate at time t with probability
    /// λ(t)/λmax. The kept arrivals form a Poisson process with rate λ(t). Every draw comes from
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
            if (rng.NextDouble() * lambdaMax >= RatePerSecondAt(t)) continue; // thinned out

            var (from, to) = Trip(floors, rng);
            yield return new PassengerSpec(SimTime.FromSeconds(t), from, to);
        }
    }

    private (int From, int To) Trip(int floors, Random rng)
    {
        var u = rng.NextDouble() * Mix.Total;
        if (u < Mix.FromLobby) return (Lobby, UpperFloor(floors, rng, exclude: Lobby));
        if (u < Mix.FromLobby + Mix.ToLobby) return (UpperFloor(floors, rng, exclude: Lobby), Lobby);

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
        if (Mix.FromLobby < 0 || Mix.ToLobby < 0 || Mix.Interfloor < 0 || Mix.Total <= 0) errors.Add("traffic.profile.mix shares must be >= 0 and not all 0");
        if (Mix.Interfloor > 0 && floors < 3) errors.Add("interfloor traffic needs at least 3 floors");
        if (Floors.Check(Lobby, floors) is { } e) errors.Add($"traffic.profile.lobby: {e}");
        return errors;
    }
}
