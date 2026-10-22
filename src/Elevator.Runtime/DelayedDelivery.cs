using System.Threading.Channels;
using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Simulation;

namespace Elevator.Runtime;

internal sealed class DelayedDelivery(RealClock clock, Func<Envelope, CancellationToken, ValueTask> deliver)
{
    private readonly Channel<(long DueMs, long Seq, Envelope Envelope)> _incoming =
        Channel.CreateUnbounded<(long, long, Envelope)>(new UnboundedChannelOptions { SingleReader = true });

    private long _seq;

    public void Schedule(Envelope envelope) => ScheduleAt(clock.Now.AddMs(envelope.DelayMs), envelope);

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
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
