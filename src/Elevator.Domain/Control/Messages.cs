using Elevator.Domain.Events;

namespace Elevator.Domain.Control;

public enum ActorKind
{
    Dispatcher,
    Elevator,
    Passengers,
    /// <summary>Whoever submitted a request and waits for its assignment (the actor runtime's callers).</summary>
    Client,
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

// ---- To the dispatcher -----------------------------------------------------------------------------------
public sealed record SubmitRequest(Request Request) : Message;

public sealed record SnapshotPublished(ElevatorSnapshot Snapshot) : Message;

public sealed record AssignmentAcceptedMsg(int RequestId, int ElevatorId) : Message;

public sealed record AssignmentRejectedMsg(int RequestId, int ElevatorId, AssignmentReason Reason) : Message;

public sealed record AssignmentReleasedMsg(int RequestId, int ElevatorId, AssignmentReason Reason) : Message;

/// <summary>A passenger was left behind (full at the door) and presses again: same request id, same t_call.</summary>
public sealed record ReRegister(int RequestId, AssignmentReason Reason) : Message;

public sealed record RequestPickedUp(int RequestId, int ElevatorId) : Message;

public sealed record RebalanceTimer : Message;

// ---- To passengers ---------------------------------------------------------------------------------------
/// <summary>A passenger arrives and enters their destination; the passengers handler submits the request.</summary>
public sealed record PassengerArrives(Request Request) : Message;

/// <summary>
/// A passenger whose request was submitted by someone else (the actor runtime's AssignRequest). Must be posted
/// before the submission, so that every later notice about this request reaches the passengers handler after it.
/// </summary>
public sealed record PassengerWaiting(Request Request) : Message;

/// <summary>Something passengers can see: doors opening (to get off) or a lantern answering their call (to get on).</summary>
public sealed record ElevatorNotice(DomainEvent Event) : Message;

// ---- To clients ------------------------------------------------------------------------------------------
public sealed record RequestAssigned(int RequestId, int ElevatorId) : Message;
