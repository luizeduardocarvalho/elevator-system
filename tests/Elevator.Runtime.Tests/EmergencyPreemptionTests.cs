using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Runtime.Tests;

public class EmergencyPreemptionTests
{
    [Fact]
    public async Task An_emergency_behind_50_queued_commands_takes_effect_first()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Floors: 20, Elevators: 2, Speed: 50) { StartPaused = true });
        for (var i = 0; i < 50; i++)
            system.Post(Address.Elevator(0), new Assign(Request.Create(1 + i % 19, 20, Instant.Zero, 20, id: 90_000 + i).Value!, null, Forced: true));
        system.FaultInjector.EmergencyStop(0, Instant.Zero);
        system.Resume();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (system.Events.Count(e => e is AssignmentRejected or AssignmentAccepted) < 50 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Null(system.Fault);

        var e0 = system.Events.Where(e => e is FaultCommandApplied { ElevatorId: 0 } or AssignmentAccepted { ElevatorId: 0 } or AssignmentRejected { ElevatorId: 0 }).ToList();
        Assert.IsType<FaultCommandApplied>(e0[0]);
        Assert.Equal(50, e0.Count - 1);
        Assert.All(e0.Skip(1), a => Assert.Equal(AssignmentReason.Emergency, Assert.IsType<AssignmentRejected>(a).Reason));
    }

    [Fact]
    public async Task Other_commands_queue_behind_the_mailbox()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Floors: 20, Elevators: 1, Speed: 50) { StartPaused = true });
        for (var i = 0; i < 5; i++)
            system.Post(Address.Elevator(0), new Assign(Request.Create(2, 9, Instant.Zero, 20, id: 90_000 + i).Value!, null, Forced: true));
        system.FaultInjector.RequestMaintenance(0, Instant.Zero);
        system.Resume();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!system.Events.Any(e => e is FaultCommandApplied) && DateTime.UtcNow < deadline) await Task.Delay(20);
        var e0 = system.Events.Where(e => e is FaultCommandApplied or AssignmentAccepted).ToList();
        Assert.Equal(5, e0.TakeWhile(e => e is AssignmentAccepted).Count());
    }

    [Fact]
    public async Task A_clear_posted_before_a_stop_does_not_overtake_or_get_overtaken()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Floors: 20, Elevators: 1, Speed: 50) { StartPaused = true });
        system.FaultInjector.ClearEmergency(0, Instant.Zero);
        system.FaultInjector.EmergencyStop(0, Instant.Zero);
        system.Resume();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!system.Events.Any(e => e is FaultCommandApplied) && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(100);

        Assert.Equal([FaultKind.EmergencyStop], system.Events.OfType<FaultCommandApplied>().Select(f => f.Kind));
        Assert.DoesNotContain(system.Events, e => e is EmergencyCleared);
    }

    private sealed class Probe : IHandler
    {
        public List<string> Seen { get; } = [];

        public Address Address => Address.Dispatcher;

        public Instant? NextWakeUp => null;

        public void Handle(Message message, Instant now, Outbox outbox) => Seen.Add(message switch
        {
            ApplyFault f => $"control:{f.Command.Kind}",
            SubmitRequest r => $"request:{r.Request.Id}",
            RequestPickedUp p => $"mailbox:{p.RequestId}",
            _ => message.GetType().Name,
        });
    }

    [Fact]
    public async Task Lanes_are_emergency_then_mailbox_then_new_requests_each_fifo()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Elevators: 1, KeepEvents: false));
        var requests = System.Threading.Channels.Channel.CreateUnbounded<SubmitRequest>();
        var probe = new Probe();
        var actor = new Actor(probe, system, requests.Reader);
        for (var i = 1; i <= 3; i++) requests.Writer.TryWrite(new SubmitRequest(Request.Create(2, 9, Instant.Zero, 20, id: i).Value!));
        for (var i = 1; i <= 3; i++) actor.Mailbox.Writer.TryWrite(new RequestPickedUp(i, 0));
        actor.Control.Writer.TryWrite(new ApplyFault(new FaultCommand(Instant.Zero, 0, FaultKind.EmergencyStop, null)));
        actor.Control.Writer.TryWrite(new ApplyFault(new FaultCommand(Instant.Zero, 0, FaultKind.EmergencyClear, null)));
        _ = actor.Start(system.Stopping);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (probe.Seen.Count < 8 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(["control:EmergencyStop", "control:EmergencyClear", "mailbox:1", "mailbox:2", "mailbox:3", "request:1", "request:2", "request:3"],
            probe.Seen);
    }
}
