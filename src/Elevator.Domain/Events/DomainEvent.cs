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
[JsonDerivedType(typeof(RequestSubmitted), nameof(RequestSubmitted))]
[JsonDerivedType(typeof(AssignmentProposed), nameof(AssignmentProposed))]
[JsonDerivedType(typeof(AssignmentAccepted), nameof(AssignmentAccepted))]
[JsonDerivedType(typeof(AssignmentRejected), nameof(AssignmentRejected))]
[JsonDerivedType(typeof(AssignmentReleased), nameof(AssignmentReleased))]
[JsonDerivedType(typeof(DispatchDeferred), nameof(DispatchDeferred))]
[JsonDerivedType(typeof(DuplicateSubmission), nameof(DuplicateSubmission))]
[JsonDerivedType(typeof(PassengerLeftBehind), nameof(PassengerLeftBehind))]
[JsonDerivedType(typeof(OverloadModeChanged), nameof(OverloadModeChanged))]
public abstract record DomainEvent([property: JsonPropertyName("t"), JsonPropertyOrder(-1)] SimTime At);

public sealed record SimulationStarted(
    SimTime At, string Scenario, int Seed, int Floors, string Scheduling, int Passengers,
    int Elevators = 1, string Dispatch = "none", int Capacity = int.MaxValue, double BypassThreshold = 1.0,
    string CapacityMode = "next-trip")
    : DomainEvent(At);

public sealed record SimulationEnded(SimTime At, int Delivered, int Unserved) : DomainEvent(At);

public sealed record PassengerArrived(SimTime At, int PassengerId, int From, int To) : DomainEvent(At);

public sealed record PassengerBoarded(SimTime At, int PassengerId, int ElevatorId, int Floor) : DomainEvent(At);

public sealed record PassengerAlighted(SimTime At, int PassengerId, int ElevatorId, int Floor) : DomainEvent(At);

/// <summary>A new hall call was lit. Pressing an already-lit button produces no event.</summary>
public sealed record HallCallRegistered(SimTime At, int Floor, Direction Direction, int ElevatorId = 0) : DomainEvent(At);

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

// ---- Destination dispatch (multi-elevator) ---------------------------------------------------------------

/// <summary>The dispatcher accepted a new request (first submission of this id).</summary>
public sealed record RequestSubmitted(SimTime At, int RequestId, int From, int To) : DomainEvent(At);

/// <summary>The dispatcher picked <paramref name="ElevatorId"/> and sent it the assignment.</summary>
/// <param name="Cost">Dispatch score of the chosen elevator (lower is better; meaning depends on the policy).</param>
/// <param name="EtaMs">Estimated sim time of the pickup, if the policy computes one.</param>
public sealed record AssignmentProposed(SimTime At, int RequestId, int ElevatorId, double Cost, long? EtaMs, bool Forced) : DomainEvent(At);

public sealed record AssignmentAccepted(SimTime At, int RequestId, int ElevatorId) : DomainEvent(At);

public sealed record AssignmentRejected(SimTime At, int RequestId, int ElevatorId, AssignmentReason Reason) : DomainEvent(At);

/// <summary>An elevator handed back an accepted assignment (full-load bypass, rebalancing).</summary>
public sealed record AssignmentReleased(SimTime At, int RequestId, int ElevatorId, AssignmentReason Reason) : DomainEvent(At);

/// <summary>No elevator is eligible right now; the request waits for the next snapshot change.</summary>
public sealed record DispatchDeferred(SimTime At, int RequestId) : DomainEvent(At);

/// <summary>A submission with an id that is already in flight; ignored (idempotency).</summary>
public sealed record DuplicateSubmission(SimTime At, int RequestId) : DomainEvent(At);

/// <summary>The assigned elevator opened for this passenger but was too full to take them; they press again.</summary>
public sealed record PassengerLeftBehind(SimTime At, int PassengerId, int ElevatorId, int Floor) : DomainEvent(At);

/// <summary>The dispatcher entered (or left) overload mode: too many requests in flight, so it uses its cheap fallback policy.</summary>
public sealed record OverloadModeChanged(SimTime At, bool Active, int InFlight) : DomainEvent(At);
