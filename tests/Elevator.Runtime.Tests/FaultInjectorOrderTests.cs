using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Runtime.Tests;

public class FaultInjectorOrderTests
{
    private static async Task<List<FaultKind>> Applied(ElevatorSystem system, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (system.Events.OfType<FaultCommandApplied>().Count() < expected && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(200);
        return [.. system.Events.OfType<FaultCommandApplied>().Select(f => f.Kind)];
    }

    private static FaultKind Alternate(int i) => i % 2 == 0 ? FaultKind.EmergencyStop : FaultKind.EmergencyClear;

    [Fact]
    public async Task Commands_due_at_the_same_time_apply_in_injection_order()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Floors: 20, Elevators: 1, Speed: 50));
        var at = system.Clock.Now.AddMs(2_000);
        for (var i = 0; i < 20; i++) system.FaultInjector.Inject(new FaultCommand(at, 0, Alternate(i), null));

        Assert.Equal(Enumerable.Range(0, 20).Select(Alternate), await Applied(system, 20));
    }

    [Fact]
    public async Task Commands_apply_in_due_time_order_whatever_the_injection_order()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Floors: 20, Elevators: 1, Speed: 50));
        var at = system.Clock.Now.AddMs(2_000);
        for (var i = 19; i >= 0; i--) system.FaultInjector.Inject(new FaultCommand(at.AddMs(i * 50), 0, Alternate(i), null));

        Assert.Equal(Enumerable.Range(0, 20).Select(Alternate), await Applied(system, 20));
    }
}
