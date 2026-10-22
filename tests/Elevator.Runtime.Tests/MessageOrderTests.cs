using System.Collections.Concurrent;
using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Simulation;

namespace Elevator.Runtime.Tests;

public class MessageOrderTests
{
    private sealed record Numbered(int Sender, int N) : Message;

    private sealed class Probe : IHandler
    {
        public ConcurrentQueue<Numbered> Seen { get; } = new();

        public Address Address => Address.Elevator(99);

        public SimTime? NextWakeUp => null;

        public void Handle(Message message, SimTime now, Outbox outbox)
        {
            if (message is Numbered n) Seen.Enqueue(n);
        }
    }

    private static void AssertFifoPerSender(IEnumerable<Numbered> seen, int senders, int each)
    {
        var bySender = seen.GroupBy(n => n.Sender).ToDictionary(g => g.Key, g => g.Select(n => n.N).ToList());
        Assert.Equal(senders, bySender.Count);
        foreach (var (sender, ns) in bySender)
            Assert.True(ns.SequenceEqual(Enumerable.Range(0, each)), $"sender {sender} out of order or incomplete");
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 1_000 && !done(); i++) await Task.Delay(10);
        Assert.True(done(), "timed out");
    }

    [Fact]
    public async Task A_mailbox_is_fifo_per_sender_under_concurrent_senders()
    {
        const int senders = 16, each = 1_000;
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Elevators: 1, KeepEvents: false));
        var probe = new Probe();
        var actor = new Actor(probe, system);
        _ = actor.Start(system.Stopping);

        using var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, senders).Select(s => Task.Run(() =>
        {
            gate.Wait();
            for (var n = 0; n < each; n++) actor.Mailbox.Writer.TryWrite(new Numbered(s, n));
        })).ToList();
        gate.Set();
        await Task.WhenAll(tasks);

        await Until(() => probe.Seen.Count == senders * each);
        AssertFifoPerSender(probe.Seen, senders, each);
    }

    [Fact]
    public async Task Delayed_delivery_is_fifo_per_sender_for_equal_delays()
    {
        const int senders = 16, each = 500;
        var clock = new RealClock(speed: 1_000);
        var delivered = new ConcurrentQueue<Numbered>();
        using var stop = new CancellationTokenSource();
        var delays = new DelayedDelivery(clock, (envelope, _) =>
        {
            delivered.Enqueue((Numbered)envelope.Message);
            return ValueTask.CompletedTask;
        });
        var loop = delays.Run(stop.Token);

        using var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, senders).Select(s => Task.Run(() =>
        {
            gate.Wait();
            for (var n = 0; n < each; n++) delays.Schedule(new Envelope(Address.Dispatcher, new Numbered(s, n), DelayMs: 50));
        })).ToList();
        gate.Set();
        await Task.WhenAll(tasks);

        await Until(() => delivered.Count == senders * each);
        await stop.CancelAsync();
        await loop;
        AssertFifoPerSender(delivered, senders, each);
    }

    [Fact]
    public async Task Delayed_delivery_is_in_due_time_order_across_delays()
    {
        var clock = new RealClock(speed: 1_000);
        var delivered = new ConcurrentQueue<int>();
        using var stop = new CancellationTokenSource();
        var delays = new DelayedDelivery(clock, (envelope, _) =>
        {
            delivered.Enqueue(((Numbered)envelope.Message).N);
            return ValueTask.CompletedTask;
        });
        var loop = delays.Run(stop.Token);

        delays.Schedule(new Envelope(Address.Dispatcher, new Numbered(0, 3), DelayMs: 3_000));
        delays.Schedule(new Envelope(Address.Dispatcher, new Numbered(0, 1), DelayMs: 1_000));
        delays.Schedule(new Envelope(Address.Dispatcher, new Numbered(0, 2), DelayMs: 2_000));

        await Until(() => delivered.Count == 3);
        await stop.CancelAsync();
        await loop;
        Assert.Equal([1, 2, 3], delivered);
    }
}
