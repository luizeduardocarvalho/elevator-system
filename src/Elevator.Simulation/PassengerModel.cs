using Elevator.Domain;

namespace Elevator.Simulation;

/// <summary>
/// Where every passenger is: waiting on a floor, riding, or delivered. Riders behave like real people: they
/// board only when the lantern shows their direction, and get off at their floor.
/// </summary>
public sealed class PassengerModel
{
    private readonly List<Passenger> _waiting = []; // arrival order
    private readonly List<(int ElevatorId, Passenger Passenger)> _riding = [];

    public int Delivered { get; private set; }

    public bool Empty => _waiting.Count == 0 && _riding.Count == 0;

    public void Arrive(Passenger passenger) => _waiting.Add(passenger);

    public IReadOnlyList<Passenger> Alight(int elevatorId, int floor)
    {
        var leaving = _riding.Where(r => r.ElevatorId == elevatorId && r.Passenger.To == floor).Select(r => r.Passenger).ToList();
        _riding.RemoveAll(r => r.ElevatorId == elevatorId && r.Passenger.To == floor);
        Delivered += leaving.Count;
        return leaving;
    }

    public IReadOnlyList<Passenger> Board(int elevatorId, int floor, Direction direction)
    {
        var boarding = _waiting.Where(p => p.From == floor && p.Direction == direction).ToList();
        _waiting.RemoveAll(p => p.From == floor && p.Direction == direction);
        _riding.AddRange(boarding.Select(p => (elevatorId, p)));
        return boarding;
    }
}
