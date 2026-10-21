using Elevator.Domain;

namespace Elevator.Simulation;

/// <param name="Elevators">Number of elevators; all start idle at the lowest floor.</param>
/// <param name="Capacity">Per elevator, in load units (a person is 1).</param>
/// <param name="BypassThreshold">Fraction of capacity at which an elevator stops taking pickups (full-load bypass).</param>
/// <param name="Profile">Time-varying random traffic (up-peak, down-peak, lunch...).</param>
public sealed record Scenario(
    string Name,
    int Seed,
    int Floors,
    ElevatorTiming Timing,
    string Scheduling,
    SimTime MaxSimTime,
    IReadOnlyList<PassengerSpec> Passengers,
    PoissonTraffic? Traffic,
    int Elevators = 1,
    int Capacity = 10,
    double BypassThreshold = 0.8,
    string Dispatch = "nearest",
    TrafficProfile? Profile = null,
    MessageLatency? Latency = null);

/// <summary>An explicitly scripted passenger: arrives at <paramref name="At"/> on <paramref name="From"/>, wants <paramref name="To"/>.</summary>
public sealed record PassengerSpec(SimTime At, int From, int To);

public sealed record Passenger(int Id, SimTime ArrivesAt, int From, int To)
{
    public Direction Direction => Directions.Between(From, To)!.Value;

    public Request ToRequest() => new(From, To, Direction, ArrivesAt, Id);
}

/// <summary>Sim-time delays on control messages, to model a dispatcher working from stale snapshots (default: none).</summary>
public sealed record MessageLatency(long SnapshotMs = 0, long DispatchMs = 0)
{
    public static MessageLatency None { get; } = new();
}
