using Elevator.Domain.Control;

namespace Elevator.Domain.Tests;

/// <summary>
/// The dispatcher keeps each elevator's newest snapshot (ADR-015). In the actor runtime two delayed snapshots from one
/// elevator can arrive out of order; an older one must never replace a newer one.
/// </summary>
public class SnapshotVersionTests
{
    /// <summary>Picks the elevator nearest to the pickup floor; ties to the lowest id.</summary>
    private sealed class Nearest : IDispatchPolicy
    {
        public string Name => "nearest-stub";

        public DispatchChoice? Choose(DispatchContext c) =>
            c.Candidates.OrderBy(s => Math.Abs(s.CurrentFloor - c.Request.PickupFloor)).ThenBy(s => s.ElevatorId)
                .Select(s => new DispatchChoice(s.ElevatorId, 0, c.Now)).FirstOrDefault();
    }

    private static ElevatorSnapshot At(int id, int floor, long version) =>
        new Elevator(id, 12, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall).Snapshot() with
        {
            CurrentFloor = floor,
            Version = version,
        };

    private static int ProposedTo(Outbox o) => o.Messages.Last(m => m.Message is Assign).To.Index;

    [Fact]
    public void An_older_snapshot_arriving_late_does_not_replace_a_newer_one()
    {
        var d = new DispatcherHandler(new Nearest(), 2, ElevatorTiming.Default);
        var o = new Outbox();
        d.Handle(new SnapshotPublished(At(0, 1, 1)), SimTime.Zero, o);
        d.Handle(new SnapshotPublished(At(1, 9, 5)), SimTime.Zero, o);
        d.Handle(new SnapshotPublished(At(1, 1, 3)), SimTime.Zero, o); // delayed, older: must be ignored

        d.Handle(new SubmitRequest(Request.Create(9, 2, SimTime.Zero, 12, id: 1).Value!), SimTime.Zero, o);

        Assert.Equal(1, ProposedTo(o)); // E1 is at floor 9 (version 5), not floor 1 (version 3)
    }

    [Fact]
    public void A_newer_snapshot_replaces_the_older_one()
    {
        var d = new DispatcherHandler(new Nearest(), 2, ElevatorTiming.Default);
        var o = new Outbox();
        d.Handle(new SnapshotPublished(At(0, 1, 1)), SimTime.Zero, o);
        d.Handle(new SnapshotPublished(At(1, 1, 3)), SimTime.Zero, o);
        d.Handle(new SnapshotPublished(At(1, 9, 5)), SimTime.Zero, o);

        d.Handle(new SubmitRequest(Request.Create(9, 2, SimTime.Zero, 12, id: 1).Value!), SimTime.Zero, o);

        Assert.Equal(1, ProposedTo(o));
    }
}
