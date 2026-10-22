using Elevator.Domain;

namespace Elevator.Simulation;

/// <summary>
/// Random passenger arrivals at a constant average rate (a homogeneous Poisson process), uniform origin and
/// destination.
/// </summary>
public sealed record PoissonTraffic(double RatePerMinute, double DurationS, double StartS = 0)
{
    /// <summary>
    /// All randomness comes from <paramref name="rng"/>, so a seed fixes the whole arrival sequence.
    /// </summary>
    public IEnumerable<PassengerSpec> Generate(int floors, Random rng)
    {
        var ratePerSecond = RatePerMinute / 60.0;
        var end = StartS + DurationS;
        var t = StartS;
        while (true)
        {
            t += -Math.Log(1 - rng.NextDouble()) / ratePerSecond;
            if (t >= end) yield break;

            var from = rng.Next(Floors.Lowest, floors + 1);
            int to;
            do to = rng.Next(Floors.Lowest, floors + 1); while (to == from);
            yield return new PassengerSpec(SimTime.FromSeconds(t), from, to);
        }
    }
}
