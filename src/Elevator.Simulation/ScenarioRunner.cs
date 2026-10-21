using Elevator.Domain;

namespace Elevator.Simulation;

/// <summary>Entry point for headless runs; the work happens in <see cref="SimEngine"/>.</summary>
public static class ScenarioRunner
{
    public const long DefaultTickMs = 100;

    /// <summary>
    /// The scenario's passengers: scripted ones plus generated traffic, ordered by arrival (scripted first on ties)
    /// and numbered from 1. The single seeded RNG is used here and nowhere else, so every strategy sees exactly the
    /// same passengers for a given seed (common random numbers).
    /// </summary>
    public static IReadOnlyList<Passenger> BuildPassengers(Scenario scenario)
    {
        var rng = new Random(scenario.Seed);
        var poisson = scenario.Traffic?.Generate(scenario.Floors, rng).ToList() ?? [];
        var profile = scenario.Profile?.Generate(scenario.Floors, rng).ToList() ?? [];
        return scenario.Passengers.Concat(poisson).Concat(profile)
            .Select((spec, order) => (spec, order))
            .OrderBy(x => x.spec.At).ThenBy(x => x.order)
            .Select((x, i) => new Passenger(i + 1, x.spec.At, x.spec.From, x.spec.To))
            .ToList();
    }

    public static SimulationResult Run(Scenario scenario, Strategy? strategy = null, long tickMs = DefaultTickMs) =>
        SimEngine.Run(scenario, strategy, tickMs);
}
