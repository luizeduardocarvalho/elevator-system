using System.Threading.Channels;
using Elevator.Domain;
using Elevator.Domain.Control;

namespace Elevator.Runtime;

/// <summary>
/// A single-writer actor: one loop owns one handler and is the only code that ever touches it. Messages arrive on
/// the actor's own mailbox (and, for the dispatcher, the bounded request channel). Between messages the loop sleeps
/// until the handler's next wake-up (an elevator's phase deadline) or the next message, whichever comes first.
/// There is no shared mutable state and no lock anywhere: other actors only ever see messages and immutable snapshots.
/// </summary>
internal sealed class Actor(IHandler handler, ElevatorSystem system, ChannelReader<SubmitRequest>? requests = null)
{
    private static readonly WakeUp WakeUpMessage = new();

    public Channel<Message> Mailbox { get; } = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Priority control channel (ADR-013): read before the mailbox, so an emergency stop takes effect before anything
    /// already queued. Still a message to the single owner of the handler: no flag shared across threads, no lock.
    /// </summary>
    public Channel<Message> Control { get; } = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });

    public IHandler Handler => handler;

    /// <summary>Messages handled and Stopwatch ticks spent inside the handler (written by this actor's loop only).</summary>
    public long MessagesHandled { get; private set; }

    public long HandlerTicks { get; private set; }

    /// <summary>Ticks spent delivering this actor's outgoing messages (channel writes, ack completion).</summary>
    public long DeliveryTicks { get; private set; }

    public Task Start(CancellationToken ct) => Task.Run(() => Loop(ct), ct);

    private async Task Loop(CancellationToken ct)
    {
        var outbox = new Outbox();
        var last = SimTime.Zero;
        while (!ct.IsCancellationRequested)
        {
            Message? message = null;
            if (Control.Reader.TryRead(out var c)) message = c;                 // emergency stops before anything queued
            else if (Mailbox.Reader.TryRead(out var m)) message = m;            // control traffic first
            else if (requests is not null && requests.TryRead(out var r)) message = r;
            else if (handler.NextWakeUp is { } wake && system.Clock.Now >= wake) message = WakeUpMessage;

            if (message is null)
            {
                await WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            var now = system.Clock.Now;
            if (now < last) now = last; // handlers need monotonic time; Stopwatch is monotonic, this is belt and braces
            last = now;

            outbox.Clear();
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            handler.Handle(message, now, outbox);
            var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var e in outbox.Events) system.Record(e);
            foreach (var envelope in outbox.Messages) await system.Deliver(envelope, ct).ConfigureAwait(false);
            HandlerTicks += t1 - t0;
            DeliveryTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            MessagesHandled++;
        }
    }

    /// <summary>Sleeps until a message is available or the next wake-up time arrives.</summary>
    private async Task WaitAsync(CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (handler.NextWakeUp is { } wake) wait.CancelAfter(system.Clock.WallUntil(wake));
        try
        {
            var mailbox = Mailbox.Reader.WaitToReadAsync(wait.Token).AsTask();
            var control = Control.Reader.WaitToReadAsync(wait.Token).AsTask();
            if (requests is null) await Task.WhenAny(mailbox, control).ConfigureAwait(false);
            else await Task.WhenAny(mailbox, control, requests.WaitToReadAsync(wait.Token).AsTask()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // the wake-up time arrived
        }
    }
}
