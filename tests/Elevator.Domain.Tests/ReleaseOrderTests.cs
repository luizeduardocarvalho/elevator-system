using Elevator.Domain.Control;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Tests;

public class ReleaseOrderTests
{
    private static Request R(int id, int from) => Request.Create(from, from + 2, Instant.Zero, 12, id: id).Value!;

    private static ElevatorController Make(long latencyMs, int capacity = 10)
    {
        var elevator = new Elevator(0, 12, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall,
            capacity: capacity, bypassThreshold: 1.0);
        var h = new ElevatorController(elevator, new ElevatorControllerOptions(SnapshotDelayMs: latencyMs));
        var o = new Outbox();
        h.Handle(new Start(), Instant.Zero, o);
        h.Handle(new Assign(R(1, 5), null, Forced: false), Instant.Zero, o);
        h.Handle(new Assign(R(2, 7), null, Forced: false), Instant.Zero, o);
        return h;
    }

    private static void AssertSnapshotTravelsFirst(Outbox o, int expectedReleases)
    {
        var toDispatcher = o.Messages.Select((m, i) => (m, i)).Where(x => x.m.To.Kind == ActorKind.Dispatcher).ToList();
        var releases = toDispatcher.Where(x => x.m.Message is AssignmentReleasedMsg).ToList();
        Assert.Equal(expectedReleases, releases.Count);
        var snapshot = toDispatcher.FirstOrDefault(x => x.m.Message is SnapshotPublished);
        Assert.True(snapshot.m is not null, "no snapshot published with the releases");
        foreach (var (release, index) in releases)
        {
            Assert.True(snapshot.i < index, $"release of {((AssignmentReleasedMsg)release.Message).RequestId} sent before the snapshot");
            Assert.True(release.DelayMs >= snapshot.m.DelayMs, $"release delay {release.DelayMs} ms < snapshot delay {snapshot.m.DelayMs} ms");
        }
    }

    [Theory]
    [InlineData(FaultKind.MaintenanceOn, 0)]
    [InlineData(FaultKind.MaintenanceOn, 1_500)]
    [InlineData(FaultKind.EmergencyStop, 0)]
    [InlineData(FaultKind.EmergencyStop, 1_500)]
    public void Maintenance_and_emergency(FaultKind kind, long latency)
    {
        var h = Make(latency);
        var o = new Outbox();
        h.Handle(new ApplyFault(new FaultCommand(Instant.Zero, 0, kind, null)), Instant.Zero, o);
        AssertSnapshotTravelsFirst(o, 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_500)]
    public void Watchdog_fault(long latency)
    {
        var h = Make(latency);
        var o = new Outbox();
        h.Handle(new FaultDeclared(30_000), Instant.Zero, o);
        AssertSnapshotTravelsFirst(o, 2);
    }
}
