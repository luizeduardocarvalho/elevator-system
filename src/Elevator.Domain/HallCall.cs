namespace Elevator.Domain;

/// <summary>
/// A stop to pick people up: floor + direction. In single-elevator (v1) mode this is a lit hall button and
/// <see cref="Requests"/> is empty. In destination-dispatch mode it groups the requests assigned to this elevator at
/// that floor and direction: several requests share one stop (hall-call dedup).
/// </summary>
public sealed record HallCall(int Floor, Direction Direction, SimTime RegisteredAt, IReadOnlyList<Request> Requests)
{
    public HallCall(int floor, Direction direction, SimTime registeredAt) : this(floor, direction, registeredAt, []) { }

    public int Load => Requests.Sum(r => r.Load);
}
