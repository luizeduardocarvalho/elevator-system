using System.Text.Json.Serialization;

namespace Elevator.Domain.Events;

/// <summary>
/// Base of everything written to the event log (JSONL). The domain only produces these as return values;
/// writing them anywhere is the shell's job. Metrics, replay and the TUI consume them.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SimulationStarted), nameof(SimulationStarted))]
[JsonDerivedType(typeof(SimulationEnded), nameof(SimulationEnded))]
[JsonDerivedType(typeof(PassengerArrived), nameof(PassengerArrived))]
[JsonDerivedType(typeof(PassengerBoarded), nameof(PassengerBoarded))]
[JsonDerivedType(typeof(PassengerAlighted), nameof(PassengerAlighted))]
[JsonDerivedType(typeof(HallCallRegistered), nameof(HallCallRegistered))]
[JsonDerivedType(typeof(HallCallAnswered), nameof(HallCallAnswered))]
[JsonDerivedType(typeof(CarCallRegistered), nameof(CarCallRegistered))]
[JsonDerivedType(typeof(RequestRejected), nameof(RequestRejected))]
[JsonDerivedType(typeof(StateChanged), nameof(StateChanged))]
[JsonDerivedType(typeof(FloorReached), nameof(FloorReached))]
[JsonDerivedType(typeof(DoorsOpened), nameof(DoorsOpened))]
public abstract record DomainEvent([property: JsonPropertyName("t"), JsonPropertyOrder(-1)] SimTime At);

public sealed record SimulationStarted(SimTime At, string Scenario, int Seed, int Floors, string Scheduling, int Passengers)
    : DomainEvent(At);

public sealed record SimulationEnded(SimTime At, int Delivered, int Unserved) : DomainEvent(At);

public sealed record PassengerArrived(SimTime At, int PassengerId, int From, int To) : DomainEvent(At);

public sealed record PassengerBoarded(SimTime At, int PassengerId, int ElevatorId, int Floor) : DomainEvent(At);

public sealed record PassengerAlighted(SimTime At, int PassengerId, int ElevatorId, int Floor) : DomainEvent(At);

/// <summary>A new hall call was lit. Pressing an already-lit button produces no event.</summary>
public sealed record HallCallRegistered(SimTime At, int Floor, Direction Direction) : DomainEvent(At);

/// <summary>The elevator has its doors open at <paramref name="Floor"/> with the lantern showing <paramref name="Direction"/>: waiting riders for that direction may board.</summary>
public sealed record HallCallAnswered(SimTime At, int ElevatorId, int Floor, Direction Direction) : DomainEvent(At);

public sealed record CarCallRegistered(SimTime At, int ElevatorId, int Floor) : DomainEvent(At);

/// <param name="Kind">"HallCall" or "CarCall".</param>
public sealed record RequestRejected(SimTime At, string Kind, int Floor, Direction? Direction, string Reason) : DomainEvent(At);

public sealed record StateChanged(SimTime At, int ElevatorId, ElevatorState From, ElevatorState To, Trigger Trigger, int Floor)
    : DomainEvent(At);

/// <summary>The elevator has arrived level with <paramref name="Floor"/> (it may or may not stop there).</summary>
public sealed record FloorReached(SimTime At, int ElevatorId, int Floor) : DomainEvent(At);

/// <summary>Doors are fully open at <paramref name="Floor"/>. Riders for this floor get off.</summary>
public sealed record DoorsOpened(SimTime At, int ElevatorId, int Floor, Direction? Lantern) : DomainEvent(At);
