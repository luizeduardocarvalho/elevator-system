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
}
