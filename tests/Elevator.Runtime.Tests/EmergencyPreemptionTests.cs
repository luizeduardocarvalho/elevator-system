using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Runtime.Tests;

/// <summary>The actor runtime's priority control channel (ADR-013).</summary>
public class EmergencyPreemptionTests
{
    [Fact]
    public async Task An_emergency_behind_50_queued_commands_takes_effect_first()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Floors: 20, Elevators: 2, Speed: 50) { StartPaused = true });
        // Pre-fill E0's mailbox with 50 assignments, then post the emergency, all before any actor runs.
        for (var i = 0; i < 50; i++)
            system.Post(Address.Elevator(0), new Assign(Request.Create(1 + i % 19, 20, SimTime.Zero, 20, id: 90_000 + i).Value!, null, Forced: true));
        system.FaultInjector.EmergencyStop(0, SimTime.Zero);
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
            system.Post(Address.Elevator(0), new Assign(Request.Create(2, 9, SimTime.Zero, 20, id: 90_000 + i).Value!, null, Forced: true));
        system.FaultInjector.RequestMaintenance(0, SimTime.Zero);
        system.Resume();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!system.Events.Any(e => e is FaultCommandApplied) && DateTime.UtcNow < deadline) await Task.Delay(20);
        var e0 = system.Events.Where(e => e is FaultCommandApplied or AssignmentAccepted).ToList();
        Assert.Equal(5, e0.TakeWhile(e => e is AssignmentAccepted).Count()); // accepted first, then maintenance
    }

    /// <summary>
    /// Emergency commands share one lane and keep their order among themselves (ADR-015): a clear posted before a stop
    /// is applied before it, so the elevator ends up stopped. (With only the stop on the priority lane, the stop
    /// overtook the clear and the elevator ended up running.)
    /// </summary>
    [Fact]
    public async Task A_clear_posted_before_a_stop_does_not_overtake_or_get_overtaken()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Floors: 20, Elevators: 1, Speed: 50) { StartPaused = true });
        system.FaultInjector.ClearEmergency(0, SimTime.Zero); // nothing to clear yet: a no-op, not logged
        system.FaultInjector.EmergencyStop(0, SimTime.Zero);
        system.Resume();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!system.Events.Any(e => e is FaultCommandApplied) && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(100);

        Assert.Equal([FaultKind.EmergencyStop], system.Events.OfType<FaultCommandApplied>().Select(f => f.Kind));
        Assert.DoesNotContain(system.Events, e => e is EmergencyCleared);
    }

    /// <summary>Records what it handles, in order.</summary>
    private sealed class Probe : IHandler
    {
        public List<string> Seen { get; } = [];

        public Address Address => Address.Dispatcher;

        public SimTime? NextWakeUp => null;

        public void Handle(Message message, SimTime now, Outbox outbox) => Seen.Add(message switch
        {
            ApplyFault f => $"control:{f.Command.Kind}",
            SubmitRequest r => $"request:{r.Request.Id}",
            RequestPickedUp p => $"mailbox:{p.RequestId}",
            _ => message.GetType().Name,
        });
    }

    /// <summary>
    /// The runtime's lanes (ADR-015): emergency commands first, then the mailbox (control traffic: snapshots, acks,
    /// releases), then new requests; FIFO within each lane. The mailbox-before-requests rule is urgency: the
    /// dispatcher brings its view up to date before taking new work.
    /// </summary>
    [Fact]
    public async Task Lanes_are_emergency_then_mailbox_then_new_requests_each_fifo()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Elevators: 1, KeepEvents: false));
        var requests = System.Threading.Channels.Channel.CreateUnbounded<SubmitRequest>();
        var probe = new Probe();
        var actor = new Actor(probe, system, requests.Reader);
        for (var i = 1; i <= 3; i++) requests.Writer.TryWrite(new SubmitRequest(Request.Create(2, 9, SimTime.Zero, 20, id: i).Value!));
        for (var i = 1; i <= 3; i++) actor.Mailbox.Writer.TryWrite(new RequestPickedUp(i, 0));
        actor.Control.Writer.TryWrite(new ApplyFault(new FaultCommand(SimTime.Zero, 0, FaultKind.EmergencyStop, null)));
        actor.Control.Writer.TryWrite(new ApplyFault(new FaultCommand(SimTime.Zero, 0, FaultKind.EmergencyClear, null)));
        _ = actor.Start(system.Stopping);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (probe.Seen.Count < 8 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(["control:EmergencyStop", "control:EmergencyClear", "mailbox:1", "mailbox:2", "mailbox:3", "request:1", "request:2", "request:3"],
            probe.Seen);
    }
}
