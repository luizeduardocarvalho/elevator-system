using Elevator.Domain;

namespace Elevator.Simulation;

public sealed record Scenario(
    string Name,
    int Seed,
    int Floors,
    ElevatorTiming Timing,
    string Scheduling,
    SimTime MaxSimTime,
    IReadOnlyList<PassengerSpec> Passengers,
    PoissonTraffic? Traffic);

/// <summary>An explicitly scripted passenger: arrives at <paramref name="At"/> on <paramref name="From"/>, wants <paramref name="To"/>.</summary>
public sealed record PassengerSpec(SimTime At, int From, int To);

public sealed record Passenger(int Id, SimTime ArrivesAt, int From, int To)
{
    public Direction Direction => Directions.Between(From, To)!.Value;
}
