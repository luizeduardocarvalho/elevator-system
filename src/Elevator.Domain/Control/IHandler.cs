using Elevator.Domain.Events;

namespace Elevator.Domain.Control;

public sealed class Outbox
{
    public List<Envelope> Messages { get; } = [];

    public List<DomainEvent> Events { get; } = [];

    public void Send(Address to, Message message, long delayMs = 0) => Messages.Add(new Envelope(to, message, delayMs));

    public void Emit(DomainEvent e) => Events.Add(e);

    public void Clear()
    {
        Messages.Clear();
        Events.Clear();
    }
}

/// <summary>
/// All decision logic lives in handlers. They are single-threaded, pure (no clock, no I/O, no threads) and are
/// driven by whichever shell owns them: the deterministic sim engine or an actor in Elevator.Runtime.
/// </summary>
public interface IHandler
{
    Address Address { get; }

    void Handle(Message message, Instant now, Outbox outbox);

    /// <summary>When this handler next needs a <see cref="WakeUp"/> even if no message arrives.</summary>
    Instant? NextWakeUp { get; }
}
