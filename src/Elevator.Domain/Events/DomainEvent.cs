using System.Text.Json.Serialization;
using Elevator.Domain.Faults;

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
[JsonDerivedType(typeof(MaintenanceRequested), nameof(MaintenanceRequested))]
[JsonDerivedType(typeof(MaintenanceEntered), nameof(MaintenanceEntered))]
[JsonDerivedType(typeof(MaintenanceExited), nameof(MaintenanceExited))]
[JsonDerivedType(typeof(EmergencyStopped), nameof(EmergencyStopped))]
[JsonDerivedType(typeof(EmergencyCleared), nameof(EmergencyCleared))]
[JsonDerivedType(typeof(FaultDetected), nameof(FaultDetected))]
[JsonDerivedType(typeof(FaultRecovered), nameof(FaultRecovered))]
[JsonDerivedType(typeof(AccessDenied), nameof(AccessDenied))]
[JsonDerivedType(typeof(FaultCommandApplied), nameof(FaultCommandApplied))]
[JsonDerivedType(typeof(DispatchEvaluated), nameof(DispatchEvaluated))]
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

// ---- Shared v3 contract (docs/contract-v3.md): emitted by the Hard stage; DispatchEvaluated on request -------

/// <summary>Maintenance requested: the elevator stops taking assignments and drains (delivers its riders).</summary>
public sealed record MaintenanceRequested(SimTime At, int ElevatorId) : DomainEvent(At);

/// <summary>Riders delivered, parked out of service at <paramref name="Floor"/>.</summary>
public sealed record MaintenanceEntered(SimTime At, int ElevatorId, int Floor) : DomainEvent(At);

public sealed record MaintenanceExited(SimTime At, int ElevatorId) : DomainEvent(At);

public sealed record EmergencyStopped(SimTime At, int ElevatorId, int Floor) : DomainEvent(At);

public sealed record EmergencyCleared(SimTime At, int ElevatorId) : DomainEvent(At);

/// <summary>The watchdog saw no progress for <paramref name="SilentForMs"/>; the elevator's calls are reassigned.</summary>
public sealed record FaultDetected(SimTime At, int ElevatorId, int Floor, long SilentForMs) : DomainEvent(At);

/// <summary>The elevator moved again after a fault.</summary>
public sealed record FaultRecovered(SimTime At, int ElevatorId) : DomainEvent(At);

/// <summary>
/// An access policy refused a request: for one elevator (<paramref name="ElevatorId"/>), or for all of them (null).
/// </summary>
public sealed record AccessDenied(SimTime At, int RequestId, int? ElevatorId, string Policy, string Reason) : DomainEvent(At);

/// <summary>A fault or operator command took effect (injected live or from a scenario's fault schedule).</summary>
public sealed record FaultCommandApplied(SimTime At, int ElevatorId, FaultKind Kind, long? DurationMs) : DomainEvent(At);

/// <summary>One elevator's score in a dispatch decision; <paramref name="Cost"/> null = ineligible (∞).</summary>
public sealed record CandidateCost(int ElevatorId, double? Cost, string? Note = null);

/// <summary>
/// Every candidate's cost for one dispatch decision and the winner (null = none eligible). Emitted just before
/// <see cref="AssignmentProposed"/>, only when explanations are enabled (SimEngineOptions.EmitDispatchEvaluations).
/// </summary>
public sealed record DispatchEvaluated(SimTime At, int RequestId, IReadOnlyList<CandidateCost> Candidates, int? WinnerId) : DomainEvent(At)
{
    public bool Equals(DispatchEvaluated? other) =>
        other is not null && At == other.At && RequestId == other.RequestId && WinnerId == other.WinnerId
        && Candidates.SequenceEqual(other.Candidates);

    public override int GetHashCode() => HashCode.Combine(At, RequestId, WinnerId, Candidates.Count);
}
