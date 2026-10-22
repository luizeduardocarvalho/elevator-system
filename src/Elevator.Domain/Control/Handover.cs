namespace Elevator.Domain.Control;

/// <summary>
/// What a simulation hands to the actor runtime (ADR-015): its handler objects (elevators, dispatcher, passengers) as
/// they are at <paramref name="At"/>, and the messages still in flight, each with its remaining delay. Ownership moves
/// once, before any thread starts: the engine refuses to run afterwards, so a handler never has two owners.
/// </summary>
public sealed record Handover(IReadOnlyList<IHandler> Handlers, Instant At, IReadOnlyList<Envelope> InFlight);
