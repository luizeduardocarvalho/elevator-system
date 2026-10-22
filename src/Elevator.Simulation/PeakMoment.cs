using Elevator.Domain;
using Elevator.Domain.Control;

namespace Elevator.Simulation;

/// <summary>The busiest moment of a run and what the elevators held then.</summary>
/// <param name="Outstanding">Riders plus accepted pickups, summed over the elevators.</param>
/// <param name="PerElevator">(riders, accepted pickups) of each elevator, by id.</param>
public sealed record Peak(Instant At, int Outstanding, IReadOnlyList<(int Riders, int Pickups)> PerElevator);

/// <summary>
/// Finds the moment of a scenario with the most outstanding work, to seed the steady-state throughput ramp (ADR-015):
/// the actor runtime takes over the simulation there, so every dispatch decision sees a realistically busy building.
/// Deterministic: same scenario and strategy, same moment.
/// </summary>
public static class PeakMoment
{
    /// <summary>Samples every <paramref name="stepMs"/> of sim time; the earliest maximum wins.</summary>
    public static Peak Find(Scenario scenario, Strategy? strategy = null, long stepMs = 1_000)
    {
        var engine = SimEngine.Create(scenario, strategy);
        var best = new Peak(Instant.Zero, -1, []);
        while (!engine.Finished)
        {
            engine.AdvanceTo(engine.Now.AddMs(stepMs));
            var perElevator = engine.Elevators.Select(e => (e.Riders.Count, e.HallCalls.Sum(c => c.Requests.Count))).ToList();
            var outstanding = perElevator.Sum(p => p.Item1 + p.Item2);
            if (outstanding > best.Outstanding) best = new Peak(engine.Now, outstanding, perElevator);
        }
        return best;
    }

    /// <summary>A fresh engine run to <paramref name="at"/> and handed over (each ramp step starts from its own copy).</summary>
    public static Handover HandOverAt(Scenario scenario, Instant at, Strategy? strategy = null)
    {
        var engine = SimEngine.Create(scenario, strategy);
        engine.AdvanceTo(at);
        if (engine.Now != at) throw new InvalidOperationException($"the run finished at {engine.Now}, before {at}");
        return engine.HandOver();
    }
}
