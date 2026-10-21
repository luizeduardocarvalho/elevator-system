using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Policy;

namespace Elevator.Simulation;

public sealed record SimulationResult(IReadOnlyList<DomainEvent> Events, int Delivered, int Unserved, SimTime EndedAt);

/// <summary>
/// Runs a scenario headless and deterministically.
///
/// The outer loop moves a <see cref="SimClock"/> in fixed ticks; inside each tick it is a discrete-event loop that
/// jumps to the next instant something happens (a passenger arrives, or an elevator phase ends) and handles it at
/// that exact time. The tick only bounds how far one step goes, so the event log is identical for any tick size.
/// That property is what will let the TUI run at any speed without changing results.
/// </summary>
public static class ScenarioRunner
{
    public const long DefaultTickMs = 100;

    public static IReadOnlyList<Passenger> BuildPassengers(Scenario scenario)
    {
        var rng = new Random(scenario.Seed); // the single RNG of the run
        var generated = scenario.Traffic?.Generate(scenario.Floors, rng) ?? [];
        return scenario.Passengers.Concat(generated)
            .Select((spec, order) => (spec, order))
            .OrderBy(x => x.spec.At).ThenBy(x => x.order) // stable: scripted passengers first on ties
            .Select((x, i) => new Passenger(i + 1, x.spec.At, x.spec.From, x.spec.To))
            .ToList();
    }

    public static SimulationResult Run(Scenario scenario, ISchedulingPolicy? policy = null, long tickMs = DefaultTickMs)
    {
        if (tickMs <= 0) throw new ArgumentOutOfRangeException(nameof(tickMs));
        policy ??= SchedulingPolicies.Create(scenario.Scheduling);

        var passengers = BuildPassengers(scenario);
        var run = new RunState(new ElevatorController(new ElevatorUnit(0, scenario.Floors, scenario.Timing, policy)), passengers);
        run.Log.Add(new SimulationStarted(SimTime.Zero, scenario.Name, scenario.Seed, scenario.Floors, policy.Name, passengers.Count));

        var clock = new SimClock();
        var endedAt = scenario.MaxSimTime;
        while (clock.Now < scenario.MaxSimTime)
        {
            var target = SimTime.Min(clock.Now.AddMs(tickMs), scenario.MaxSimTime);
            if (run.AdvanceTo(target) is { } finishedAt)
            {
                endedAt = finishedAt;
                break;
            }
            clock.AdvanceTo(target);
        }

        var unserved = passengers.Count - run.Model.Delivered;
        run.Log.Add(new SimulationEnded(endedAt, run.Model.Delivered, unserved));
        return new SimulationResult(run.Log, run.Model.Delivered, unserved, endedAt);
    }

    private sealed class RunState(ElevatorController controller, IReadOnlyList<Passenger> passengers)
    {
        private readonly Queue<Passenger> _arrivals = new(passengers);

        public List<DomainEvent> Log { get; } = [];

        public PassengerModel Model { get; } = new();

        private bool Finished =>
            _arrivals.Count == 0 && Model.Empty && controller.Elevator.State == ElevatorState.Idle && controller.NextDeadline is null;

        /// <summary>Processes everything up to <paramref name="target"/>; returns the instant the run finished, if it did.</summary>
        public SimTime? AdvanceTo(SimTime target)
        {
            if (Finished) return controller.Now;

            while (NextEventTime(target) is { } t)
            {
                // Fixed order at the same instant: elevator phases ending at t, then passengers arriving at t.
                Handle(controller.ProcessRequests(t), t);
                while (_arrivals.TryPeek(out var next) && next.ArrivesAt == t)
                {
                    var p = _arrivals.Dequeue();
                    Log.Add(new PassengerArrived(t, p.Id, p.From, p.To));
                    Model.Arrive(p);
                    controller.RequestElevator(p.From, p.Direction);
                    Handle(controller.ProcessRequests(t), t);
                }
                if (Finished) return t;
            }

            Handle(controller.ProcessRequests(target), target);
            return null;
        }

        private SimTime? NextEventTime(SimTime target)
        {
            SimTime? next = controller.NextDeadline;
            if (_arrivals.TryPeek(out var p) && (next is null || p.ArrivesAt < next)) next = p.ArrivesAt;
            return next <= target ? next : null;
        }

        /// <summary>Logs elevator events and lets passengers react; their car calls produce more events at the same instant.</summary>
        private void Handle(IReadOnlyList<DomainEvent> events, SimTime t)
        {
            while (events.Count > 0)
            {
                foreach (var e in events)
                {
                    Log.Add(e);
                    switch (e)
                    {
                        case DoorsOpened opened:
                            foreach (var p in Model.Alight(opened.ElevatorId, opened.Floor))
                                Log.Add(new PassengerAlighted(t, p.Id, opened.ElevatorId, opened.Floor));
                            break;
                        case HallCallAnswered answered:
                            foreach (var p in Model.Board(answered.ElevatorId, answered.Floor, answered.Direction))
                            {
                                Log.Add(new PassengerBoarded(t, p.Id, answered.ElevatorId, answered.Floor));
                                controller.SelectFloor(p.To);
                            }
                            break;
                    }
                }
                events = controller.ProcessRequests(t);
            }
        }
    }
}
