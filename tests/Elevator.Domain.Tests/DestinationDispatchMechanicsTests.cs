using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

public class DestinationDispatchMechanicsTests
{
    private static Request R(int id, int from, int to, long calledAtMs = 0) =>
        Request.Create(from, to, new SimTime(calledAtMs), Harness.Floors, id: id).Value!;

    private static Elevator Make(int capacity = 10, double threshold = 0.8, int startFloor = 1) =>
        new(0, Harness.Floors, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, startFloor, capacity, threshold);

    [Fact]
    public void Requests_at_the_same_floor_and_direction_share_one_stop()
    {
        var e = Make();
        e.EnterMaintenance();
        e.AssignPickup(R(1, 5, 9));
        e.AssignPickup(R(2, 5, 7));
        e.AssignPickup(R(3, 5, 2));

        Assert.Equal(2, e.HallCalls.Count);
        Assert.Equal([1, 2], e.HallCalls.Single(c => c.Direction == Direction.Up).Requests.Select(r => r.Id));
        Assert.Equal(2, e.DrainEvents().OfType<HallCallRegistered>().Count());
    }

    [Fact]
    public void Boarding_adds_load_and_a_drop_off_and_alighting_removes_the_load()
    {
        var e = Make();
        e.Board(R(1, 1, 6));
        Assert.Equal((1, true), (e.Load, e.CarCalls.Contains(6)));

        e.Alight(1);
        Assert.Equal(0, e.Load);
    }

    [Fact]
    public void Next_trip_a_full_elevator_keeps_its_pickups_bypasses_them_and_answers_once_riders_get_off()
    {
        var e = Make(capacity: 5, threshold: 0.8);
        e.AssignPickup(R(10, 3, 9));
        for (var i = 1; i <= 4; i++) e.Board(R(i, 1, 4));
        e.DrainEvents();
        Assert.True(e.IsFull);
        Assert.Single(e.HallCalls);

        e.Tick(new SimTime(7_000));
        var events = e.DrainEvents();
        Assert.DoesNotContain(events, ev => ev is AssignmentReleased);
        Assert.DoesNotContain(events, ev => ev is HallCallAnswered);
        var openAt4 = events.OfType<DoorsOpened>().Single(d => d.Floor == 4);

        foreach (var i in Enumerable.Range(1, 4)) e.Alight(i);
        e.Tick(new SimTime(200_000));
        var answered = Assert.Single(e.DrainEvents().OfType<HallCallAnswered>());
        Assert.Equal(3, answered.Floor);
        Assert.True(answered.At > openAt4.At);
    }

    [Fact]
    public void Unassign_removes_only_that_request_from_a_shared_stop()
    {
        var e = Make();
        e.EnterMaintenance();
        e.AssignPickup(R(1, 5, 9));
        e.AssignPickup(R(2, 5, 7));

        Assert.True(e.Unassign(1));
        Assert.False(e.Unassign(1));
        Assert.Equal([2], Assert.Single(e.HallCalls).Requests.Select(r => r.Id));
        Assert.True(e.Unassign(2));
        Assert.Empty(e.HallCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_500)]
    [InlineData(8_500)]
    [InlineData(12_300)]
    [InlineData(13_400)]
    [InlineData(17_000)]
    public void Elevator_rebuilt_from_a_snapshot_behaves_identically(long snapshotAtMs)
    {
        var original = Make();
        original.AssignPickup(R(1, 5, 9));
        original.AssignPickup(R(2, 2, 1));
        original.Tick(new SimTime(snapshotAtMs));
        original.DrainEvents();

        var copy = Elevator.FromSnapshot(original.Snapshot(), ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall);

        foreach (var e in new[] { original, copy })
        {
            e.Board(R(3, e.CurrentFloor == 10 ? 9 : e.CurrentFloor, 10));
            e.Tick(new SimTime(200_000));
        }

        Assert.Equal(original.DrainEvents(), copy.DrainEvents());
        Assert.Equal(original.Snapshot() with { HallCalls = [], CarCalls = [], Riders = [] },
            copy.Snapshot() with { HallCalls = [], CarCalls = [], Riders = [] });
    }
}
