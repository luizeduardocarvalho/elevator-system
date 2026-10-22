using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

public class ElevatorTypeTests
{
    private static Request R(int id, int from, int to) => Request.Create(from, to, Instant.Zero, Harness.Floors, id: id).Value!;

    private static readonly ElevatorTiming Fast = new(FloorTravelMs: 1_000, DoorOpeningMs: 500, DoorDwellMs: 2_000, DoorClosingMs: 500);

    private static readonly ulong Express = FloorMask.Of([1, 6, 7, 8, 9, 10]);

    private static Elevator Make(ulong served = FloorMask.All, ElevatorTiming? timing = null, int start = 1, string type = "express") =>
        new(0, Harness.Floors, timing ?? ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall, start, capacity: 10,
            servedFloors: served, typeName: type);

    [Fact]
    public void Floor_masks_parse_ranges_and_all_means_every_floor_even_above_63()
    {
        Assert.Equal(FloorMask.Of([1, 10, 11, 12]), FloorMask.Parse(["1", "10-12"], 20).Value);
        Assert.True(FloorMask.Contains(FloorMask.All, 200));
        Assert.False(FloorMask.Contains(Express, 3));
        Assert.Equal("1, 6-10", FloorMask.Describe(Express, 10));
        Assert.Contains("outside", FloorMask.Parse(["0-3"], 20).Error, StringComparison.Ordinal);
        Assert.Contains("63", FloorMask.Parse(["70"], 80).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_carries_type_served_floors_and_timing()
    {
        var s = Make(Express, Fast).Snapshot();
        Assert.Equal(("express", Express, Fast), (s.TypeName, s.ServedFloors, s.Timing));
        Assert.True(s.Serves(7));
        Assert.False(s.Serves(3));
    }

    [Fact]
    public void Default_elevator_is_standard_serving_every_floor()
    {
        var s = new Elevator(0, Harness.Floors, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall).Snapshot();
        Assert.Equal(("standard", FloorMask.All, ElevatorMode.Normal), (s.TypeName, s.ServedFloors, s.Mode));
    }

    [Fact]
    public void Uses_its_own_timing()
    {
        var e = Make(timing: Fast);
        e.Board(R(1, 1, 4));
        e.Tick(new Instant(3_000));
        Assert.Contains(e.DrainEvents(), ev => ev is StateChanged { Trigger: Trigger.ArriveAndOpen, Floor: 4, At.Ms: 3_000 });
    }

    [Fact]
    public void Passes_floors_it_does_not_serve_without_stopping()
    {
        var e = Make(Express);
        e.Board(R(1, 1, 9));
        e.Tick(new Instant(60_000));
        var events = e.DrainEvents();
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9], events.OfType<FloorReached>().Select(f => f.Floor));
        Assert.Equal([9], events.OfType<DoorsOpened>().Select(d => d.Floor));
    }

    [Fact]
    public void Losing_its_target_between_unserved_floors_it_carries_on_to_the_next_served_floor()
    {
        var e = Make(Express);
        e.AssignPickup(R(1, 8, 10));
        e.Tick(new Instant(3_000));
        Assert.True(e.Unassign(1));
        e.Tick(new Instant(60_000));
        var opened = Assert.Single(e.DrainEvents().OfType<DoorsOpened>());
        Assert.Equal(6, opened.Floor);
    }

    [Fact]
    public void Stops_at_unserved_floors_are_programming_errors()
    {
        var e = Make(Express);
        Assert.Throws<ArgumentOutOfRangeException>(() => e.AssignPickup(R(1, 3, 9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => e.AddRequest(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Express, start: 3));
    }

    [Fact]
    public void From_snapshot_keeps_type_served_floors_and_timing()
    {
        var s = Make(Express, Fast).Snapshot();
        var copy = Elevator.FromSnapshot(s, ElevatorTiming.Default, TestPolicies.NearestCarCallThenOldestHallCall).Snapshot();
        Assert.Equal((s.TypeName, s.ServedFloors, s.Timing), (copy.TypeName, copy.ServedFloors, copy.Timing));
    }
}
