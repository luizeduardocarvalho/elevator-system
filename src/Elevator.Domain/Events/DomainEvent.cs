using System.Text.Json.Serialization;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Events;

/// <summary>
/// Base of everything written to the event log (JSONL). The domain only produces these as return values;
/// writing them anywhere is the shell's job. Metrics, replay and the TUI consume them.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
// RunStarted and RunEnded keep their pre-rename wire names, so every existing log still replays (ADR-019).
[JsonDerivedType(typeof(RunStarted), "SimulationStarted")]
[JsonDerivedType(typeof(RunEnded), "SimulationEnded")]
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
public abstract record DomainEvent([property: JsonPropertyName("t"), JsonPropertyOrder(-1)] Instant At);

public sealed record RunStarted(
    Instant At, string Scenario, int Seed, int Floors, string Scheduling, int Passengers,
    int Elevators = 1, string Dispatch = "none", int Capacity = int.MaxValue, double BypassThreshold = 1.0,
    string CapacityMode = "next-trip",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FleetEntry>? Fleet = null)
    : DomainEvent(At)
{
    public bool Equals(RunStarted? other) =>
        other is not null && At == other.At && Scenario == other.Scenario && Seed == other.Seed && Floors == other.Floors
        && Scheduling == other.Scheduling && Passengers == other.Passengers && Elevators == other.Elevators
        && Dispatch == other.Dispatch && Capacity == other.Capacity && BypassThreshold.Equals(other.BypassThreshold)
        && CapacityMode == other.CapacityMode
        && (Fleet is null ? other.Fleet is null : other.Fleet is not null && Fleet.SequenceEqual(other.Fleet));

    public override int GetHashCode() => HashCode.Combine(At, Scenario, Seed, Floors, Passengers, Elevators, Fleet?.Count);
}

public sealed record FleetEntry(int ElevatorId, string Type, int Capacity, double BypassThreshold, ulong ServedFloors);

public sealed record RunEnded(Instant At, int Delivered, int Unserved) : DomainEvent(At);

/// <param name="Load">v3.1 contract: load units when not 1 (null = a person, 1 unit). Written only when set.</param>
/// <param name="Vip">v3.1 contract: true for a VIP passenger; null otherwise. Written only when set.</param>
/// <param name="AccessLevel">v3.1 contract: access level when above 0 (restricted floors); null = 0. Written only when set.</param>
public sealed record PassengerArrived(
    Instant At, int PassengerId, int From, int To,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Load = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Vip = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AccessLevel = null)
    : DomainEvent(At)
{
    [JsonIgnore]
    public int LoadUnits => Load ?? 1;

    [JsonIgnore]
    public bool IsVip => Vip == true;
}

public sealed record PassengerBoarded(Instant At, int PassengerId, int ElevatorId, int Floor) : DomainEvent(At);

public sealed record PassengerAlighted(Instant At, int PassengerId, int ElevatorId, int Floor) : DomainEvent(At);

/// <summary>A new hall call was lit. Pressing an already-lit button produces no event.</summary>
public sealed record HallCallRegistered(Instant At, int Floor, Direction Direction, int ElevatorId = 0) : DomainEvent(At);

/// <summary>The elevator has its doors open at <paramref name="Floor"/> with the lantern showing <paramref name="Direction"/>: waiting riders for that direction may board.</summary>
public sealed record HallCallAnswered(Instant At, int ElevatorId, int Floor, Direction Direction) : DomainEvent(At);

public sealed record CarCallRegistered(Instant At, int ElevatorId, int Floor) : DomainEvent(At);

/// <param name="Kind">"HallCall" or "CarCall".</param>
public sealed record RequestRejected(Instant At, string Kind, int Floor, Direction? Direction, string Reason) : DomainEvent(At);

public sealed record StateChanged(Instant At, int ElevatorId, ElevatorState From, ElevatorState To, Trigger Trigger, int Floor)
    : DomainEvent(At);

/// <summary>The elevator has arrived level with <paramref name="Floor"/> (it may or may not stop there).</summary>
public sealed record FloorReached(Instant At, int ElevatorId, int Floor) : DomainEvent(At);

/// <summary>Doors are fully open at <paramref name="Floor"/>. Riders for this floor get off.</summary>
public sealed record DoorsOpened(Instant At, int ElevatorId, int Floor, Direction? Lantern) : DomainEvent(At);

/// <summary>The dispatcher accepted a new request (first submission of this id).</summary>
public sealed record RequestSubmitted(Instant At, int RequestId, int From, int To) : DomainEvent(At);

/// <param name="Cost">Dispatch score of the chosen elevator (lower is better; meaning depends on the policy).</param>
/// <param name="EtaMs">Estimated sim time of the pickup, if the policy computes one.</param>
public sealed record AssignmentProposed(Instant At, int RequestId, int ElevatorId, double Cost, long? EtaMs, bool Forced) : DomainEvent(At);

public sealed record AssignmentAccepted(Instant At, int RequestId, int ElevatorId) : DomainEvent(At);

public sealed record AssignmentRejected(Instant At, int RequestId, int ElevatorId, AssignmentReason Reason) : DomainEvent(At);

/// <summary>An elevator handed back an accepted assignment (full-load bypass, rebalancing).</summary>
public sealed record AssignmentReleased(Instant At, int RequestId, int ElevatorId, AssignmentReason Reason) : DomainEvent(At);

/// <summary>No elevator is eligible right now; the request waits for the next snapshot change.</summary>
public sealed record DispatchDeferred(Instant At, int RequestId) : DomainEvent(At);

/// <summary>A submission with an id that is already in flight; ignored (idempotency).</summary>
public sealed record DuplicateSubmission(Instant At, int RequestId) : DomainEvent(At);

/// <summary>The assigned elevator opened for this passenger but was too full to take them; they press again.</summary>
public sealed record PassengerLeftBehind(Instant At, int PassengerId, int ElevatorId, int Floor) : DomainEvent(At);

/// <summary>The dispatcher entered (or left) overload mode: too many requests in flight, so it uses its cheap fallback policy.</summary>
public sealed record OverloadModeChanged(Instant At, bool Active, int InFlight) : DomainEvent(At);

/// <summary>Maintenance requested: the elevator stops taking assignments and drains (delivers its riders).</summary>
public sealed record MaintenanceRequested(Instant At, int ElevatorId) : DomainEvent(At);

/// <summary>Riders delivered, parked out of service at <paramref name="Floor"/>.</summary>
public sealed record MaintenanceEntered(Instant At, int ElevatorId, int Floor) : DomainEvent(At);

public sealed record MaintenanceExited(Instant At, int ElevatorId) : DomainEvent(At);

public sealed record EmergencyStopped(Instant At, int ElevatorId, int Floor) : DomainEvent(At);

public sealed record EmergencyCleared(Instant At, int ElevatorId) : DomainEvent(At);

/// <summary>The watchdog saw no progress for <paramref name="SilentForMs"/>; the elevator's calls are reassigned.</summary>
public sealed record FaultDetected(Instant At, int ElevatorId, int Floor, long SilentForMs) : DomainEvent(At);

/// <summary>The elevator moved again after a fault.</summary>
public sealed record FaultRecovered(Instant At, int ElevatorId) : DomainEvent(At);

/// <summary>
/// An access policy refused a request: for one elevator (<paramref name="ElevatorId"/>), or for all of them (null).
/// </summary>
public sealed record AccessDenied(Instant At, int RequestId, int? ElevatorId, string Policy, string Reason) : DomainEvent(At);

/// <summary>A fault or operator command took effect (injected live or from a scenario's fault schedule).</summary>
public sealed record FaultCommandApplied(Instant At, int ElevatorId, FaultKind Kind, long? DurationMs) : DomainEvent(At);

/// <summary>One elevator's score in a dispatch decision; <paramref name="Cost"/> null = ineligible (∞).</summary>
public sealed record CandidateCost(int ElevatorId, double? Cost, string? Note = null);

/// <summary>
/// Every candidate's cost for one dispatch decision and the winner (null = none eligible). Emitted just before
/// <see cref="AssignmentProposed"/>, only when explanations are enabled (SimEngineOptions.EmitDispatchEvaluations).
/// </summary>
public sealed record DispatchEvaluated(Instant At, int RequestId, IReadOnlyList<CandidateCost> Candidates, int? WinnerId) : DomainEvent(At)
{
    public bool Equals(DispatchEvaluated? other) =>
        other is not null && At == other.At && RequestId == other.RequestId && WinnerId == other.WinnerId
        && Candidates.SequenceEqual(other.Candidates);

    public override int GetHashCode() => HashCode.Combine(At, RequestId, WinnerId, Candidates.Count);
}
