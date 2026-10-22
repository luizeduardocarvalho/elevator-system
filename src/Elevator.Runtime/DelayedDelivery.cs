using System.Threading.Channels;
using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Simulation;

namespace Elevator.Runtime;

/// <summary>
/// Link latency in the actor runtime (ADR-015): messages sent with a delay are held here and delivered in order of
/// (due sim time, enqueue sequence). One loop owns the queue (single reader, like every actor), so two messages
/// with the same delay from one sender keep their order: a delayed snapshot can no longer arrive after the release
/// it explains. This replaces one Task.Run + Task.Delay per message, which gave no ordering at all.
/// </summary>
internal sealed class DelayedDelivery(RealClock clock, Func<Envelope, CancellationToken, ValueTask> deliver)
{
    private readonly Channel<(long DueMs, long Seq, Envelope Envelope)> _incoming =
        Channel.CreateUnbounded<(long, long, Envelope)>(new UnboundedChannelOptions { SingleReader = true });

    private long _seq;

    /// <summary>Thread-safe. Due at now + DelayMs on the system clock; delivered with DelayMs = 0.</summary>
    public void Schedule(Envelope envelope) => ScheduleAt(clock.Now.AddMs(envelope.DelayMs), envelope);

    /// <summary>Thread-safe. Due at <paramref name="due"/> (sim time; now or past = as soon as possible), in order of (due, call order).</summary>
    public void ScheduleAt(SimTime due, Envelope envelope) =>
        _incoming.Writer.TryWrite((due.Ms, Interlocked.Increment(ref _seq), envelope with { DelayMs = 0 }));

    public Task Run(CancellationToken ct) => Task.Run(() => Loop(ct), CancellationToken.None);

    private async Task Loop(CancellationToken ct)
    {
        var queue = new PriorityQueue<Envelope, (long DueMs, long Seq)>();
        while (!ct.IsCancellationRequested)
        {
            while (_incoming.Reader.TryRead(out var item)) queue.Enqueue(item.Envelope, (item.DueMs, item.Seq));

            if (queue.TryPeek(out var next, out var key) && key.DueMs <= clock.Now.Ms)
            {
                queue.Dequeue();
                await deliver(next, ct).ConfigureAwait(false);
                continue;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (queue.Count > 0) wait.CancelAfter(clock.WallUntil(new SimTime(key.DueMs)));
            try
            {
                await _incoming.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // the earliest message is due
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
