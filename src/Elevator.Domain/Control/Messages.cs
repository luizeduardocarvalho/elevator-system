using Elevator.Domain.Events;

namespace Elevator.Domain.Control;

public enum ActorKind
{
    Dispatcher,
    Elevator,
    Passengers,
    /// <summary>Whoever submitted a request and waits for its assignment (the actor runtime's callers).</summary>
    Client,
    /// <summary>The simulator's fault scheduler: holds injected commands until their sim time (ADR-013).</summary>
    FaultScheduler,
}

public readonly record struct Address(ActorKind Kind, int Index = 0)
{
    public static Address Dispatcher => new(ActorKind.Dispatcher);

    public static Address Passengers => new(ActorKind.Passengers);

    public static Address Client => new(ActorKind.Client);

    public static Address Elevator(int id) => new(ActorKind.Elevator, id);

    public override string ToString() => Kind == ActorKind.Elevator ? $"E{Index}" : Kind.ToString();
}

/// <summary>
/// The single message protocol (ADR-005). The deterministic sim engine and the actor runtime deliver exactly these
/// messages to exactly the same handlers; they differ only in how messages travel and how time passes.
/// </summary>
public abstract record Message;

/// <summary>A message to deliver to <paramref name="To"/>, <paramref name="DelayMs"/> sim milliseconds from now.</summary>
public sealed record Envelope(Address To, Message Message, long DelayMs = 0);

// ---- To elevators ----------------------------------------------------------------------------------------
public sealed record Start : Message;

/// <summary>A timed phase may have ended; the handler ticks the elevator to "now".</summary>
public sealed record WakeUp : Message;

/// <param name="EtaEstimate">The dispatcher's pickup estimate, for the staleness check.</param>
/// <param name="Forced">Every eligible elevator rejected as stale: accept unless full or in maintenance.</param>
public sealed record Assign(Request Request, SimTime? EtaEstimate, bool Forced) : Message;

public sealed record Unassign(int RequestId) : Message;

public sealed record Boarded(Request Rider) : Message;

public sealed record Alighted(int RequestId) : Message;

/// <summary>A fault or operator command for this elevator, taking effect now (ADR-013).</summary>
public sealed record ApplyFault(Faults.FaultCommand Command) : Message;

/// <summary>The dispatcher's watchdog saw no progress for <paramref name="SilentForMs"/> while this elevator had work (ADR-013).</summary>
public sealed record FaultDeclared(long SilentForMs) : Message;

// ---- To the dispatcher -----------------------------------------------------------------------------------
public sealed record SubmitRequest(Request Request) : Message;

public sealed record SnapshotPublished(ElevatorSnapshot Snapshot) : Message;

/// <param name="Version">The elevator's version right after accepting: any snapshot at or after it includes the pickup.</param>
public sealed record AssignmentAcceptedMsg(int RequestId, int ElevatorId, long Version = 0) : Message;

public sealed record AssignmentRejectedMsg(int RequestId, int ElevatorId, AssignmentReason Reason) : Message;

public sealed record AssignmentReleasedMsg(int RequestId, int ElevatorId, AssignmentReason Reason) : Message;

/// <summary>A passenger was left behind (full at the door) and presses again: same request id, same t_call.</summary>
public sealed record ReRegister(int RequestId, AssignmentReason Reason) : Message;

public sealed record RequestPickedUp(int RequestId, int ElevatorId) : Message;

public sealed record RebalanceTimer : Message;

/// <summary>The watchdog's check is due (ADR-013). Scheduled only while some elevator has work.</summary>
public sealed record WatchdogTimer : Message;

/// <summary>
/// The elevator reached <paramref name="Floor"/> at <paramref name="At"/> (ADR-013): progress for the watchdog. Passing a
/// floor without stopping changes nothing a snapshot shows, so without this a long run between stops looks silent.
/// Only the watchdog reads it; it never triggers a dispatch decision.
/// </summary>
public sealed record ProgressReported(int ElevatorId, int Floor, SimTime At) : Message;

/// <summary>Spec's findBestElevator(): which elevator would the dispatcher choose now? Answered, nothing assigned.</summary>
public sealed record FindBestQuery(int QueryId, Request Request) : Message;

/// <summary>Spec's balanceLoad(): run a rebalancing pass now (no-op when rebalancing is off).</summary>
public sealed record RebalanceNow : Message;

/// <summary>
/// Every elevator's access policies refused this request (ADR-012): it is not dispatched, the passenger is told and
/// leaves (counted as refused by policy, never as lost). Sent to the passengers and to the client.
/// </summary>
public sealed record RequestRefused(int RequestId, string Policy, string Reason) : Message;

// ---- To passengers ---------------------------------------------------------------------------------------
/// <summary>A passenger arrives and enters their destination; the passengers handler submits the request.</summary>
public sealed record PassengerArrives(Request Request) : Message;

/// <summary>Something passengers can see: doors opening (to get off) or a lantern answering their call (to get on).</summary>
public sealed record ElevatorNotice(DomainEvent Event) : Message;

// ---- Perf only (ADR-015) ---------------------------------------------------------------------------------
/// <summary>
/// The steady-state throughput ramp retires each generated request as soon as it is accepted, so the building keeps
/// its seeded backlog instead of accumulating requests it could never serve at thousands per second. Sent by the actor
/// runtime (only with <c>RetireOnAccept</c>) to the elevator and to the passengers, never in a simulation or a real run.
/// The elevator drops the pickup without releasing it; the passengers handler tells the dispatcher with the
/// <see cref="RequestPickedUp"/> it already handles when a passenger boards.
/// </summary>
public sealed record PassengerRetired(int RequestId, int ElevatorId) : Message;

// ---- To the simulator's fault scheduler -------------------------------------------------------------------
/// <summary>Deliver <paramref name="Command"/> to its elevator at max(Command.At, now), as <see cref="ApplyFault"/>.</summary>
public sealed record ScheduleFault(Faults.FaultCommand Command) : Message;

// ---- To clients ------------------------------------------------------------------------------------------
public sealed record RequestAssigned(int RequestId, int ElevatorId) : Message;

public sealed record BestElevatorAnswer(int QueryId, int? ElevatorId, double Cost) : Message;
