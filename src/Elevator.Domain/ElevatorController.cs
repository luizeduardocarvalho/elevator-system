using Elevator.Domain.Events;

namespace Elevator.Domain;

/// <summary>
/// The spec's ElevatorController: the entry point for requests. It validates input (rejecting gracefully with a
/// <see cref="RequestRejected"/> event, never throwing) and advances the elevator.
/// </summary>
public sealed class ElevatorController(Elevator elevator)
{
    private readonly List<DomainEvent> _outbox = [];

    public Elevator Elevator { get; } = elevator;

    public SimTime Now => Elevator.Now;

    public SimTime? NextDeadline => Elevator.NextDeadline;

    public RequestResult RequestElevator(int floor, Direction direction)
    {
        if (Floors.CheckHallButton(floor, direction, Elevator.FloorCount) is { } error)
            return Reject("HallCall", floor, direction, error);

        Elevator.AddHallCall(floor, direction);
        Collect();
        return RequestResult.Ok;
    }

    public RequestResult SelectFloor(int floor)
    {
        if (Floors.Check(floor, Elevator.FloorCount) is { } error)
            return Reject("CarCall", floor, null, error);

        Elevator.AddRequest(floor);
        Collect();
        return RequestResult.Ok;
    }

    /// <summary>Advances the elevator to <paramref name="now"/> and returns every event produced since the last call, in order.</summary>
    public IReadOnlyList<DomainEvent> ProcessRequests(SimTime now)
    {
        Elevator.Tick(now);
        Collect();
        var events = _outbox.ToArray();
        _outbox.Clear();
        return events;
    }

    private RequestResult Reject(string kind, int floor, Direction? direction, string reason)
    {
        Collect();
        _outbox.Add(new RequestRejected(Now, kind, floor, direction, reason));
        return RequestResult.Rejected(reason);
    }

    private void Collect() => _outbox.AddRange(Elevator.DrainEvents());
}
