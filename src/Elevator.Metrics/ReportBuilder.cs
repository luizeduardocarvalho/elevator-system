using Elevator.Domain.Events;

namespace Elevator.Metrics;

/// <summary>Builds a <see cref="RunReport"/> from the event log alone. It never touches the elevator.</summary>
public static class ReportBuilder
{
    public static RunReport Build(IEnumerable<DomainEvent> events)
    {
        SimulationStarted? started = null;
        SimulationEnded? ended = null;
        var arrived = new Dictionary<int, long>();
        var boarded = new Dictionary<int, long>();
        var alighted = new Dictionary<int, long>();
        int floors = 0, stops = 0, rejected = 0;

        foreach (var e in events)
        {
            switch (e)
            {
                case SimulationStarted s: started = s; break;
                case SimulationEnded s: ended = s; break;
                case PassengerArrived a: arrived[a.PassengerId] = a.At.Ms; break;
                case PassengerBoarded b: boarded[b.PassengerId] = b.At.Ms; break;
                case PassengerAlighted a: alighted[a.PassengerId] = a.At.Ms; break;
                case FloorReached: floors++; break;
                case DoorsOpened: stops++; break;
                case RequestRejected: rejected++; break;
            }
        }

        var delivered = alighted.Keys.Where(id => arrived.ContainsKey(id) && boarded.ContainsKey(id)).ToList();
        var waits = delivered.Select(id => (boarded[id] - arrived[id]) / 1000.0).ToList();
        var rides = delivered.Select(id => (alighted[id] - boarded[id]) / 1000.0).ToList();
        var passengers = started?.Passengers ?? arrived.Count;

        return new RunReport(
            started?.Scenario ?? "?",
            started?.Scheduling ?? "?",
            started?.Seed ?? 0,
            passengers,
            delivered.Count,
            passengers - delivered.Count,
            rejected,
            DurationStats.From(waits),
            DurationStats.From(rides),
            floors,
            stops,
            (ended?.At.Ms ?? 0) / 1000.0);
    }
}
