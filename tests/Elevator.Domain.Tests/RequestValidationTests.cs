using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

public class RequestValidationTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(11, 5)]
    [InlineData(5, 0)]
    [InlineData(5, 11)]
    public void Request_rejects_floors_out_of_range(int pickup, int destination) =>
        Assert.Contains("outside 1..10", Request.Create(pickup, destination, SimTime.Zero, 10).Error);

    [Fact]
    public void Request_rejects_same_pickup_and_destination() =>
        Assert.False(Request.Create(4, 4, SimTime.Zero, 10).IsSuccess);

    [Fact]
    public void Request_derives_direction_and_rejects_inconsistent_declared_direction()
    {
        Assert.Equal(Direction.Down, Request.Create(7, 2, SimTime.Zero, 10).Value!.Direction);
        Assert.True(Request.Create(2, 7, SimTime.Zero, 10, Direction.Up).IsSuccess);
        Assert.Contains("inconsistent", Request.Create(2, 7, SimTime.Zero, 10, Direction.Down).Error);
    }

    [Theory]
    [InlineData(0, Direction.Up)]
    [InlineData(11, Direction.Down)]
    [InlineData(1, Direction.Down)]
    [InlineData(10, Direction.Up)]
    public void Controller_rejects_invalid_hall_calls_gracefully(int floor, Direction direction)
    {
        var h = new Harness();
        var result = h.Hall(0, floor, direction);

        Assert.False(result.Accepted);
        var rejected = Assert.Single(h.Events.OfType<RequestRejected>());
        Assert.Equal(("HallCall", floor), (rejected.Kind, rejected.Floor));
        Assert.Equal(ElevatorState.Idle, h.Elevator.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Controller_rejects_invalid_car_calls_gracefully(int floor)
    {
        var h = new Harness();
        Assert.False(h.Car(0, floor).Accepted);
        Assert.Single(h.Events.OfType<RequestRejected>());
    }

    [Fact]
    public void Pressing_a_lit_hall_button_again_registers_nothing_new()
    {
        var h = new Harness(startFloor: 5);
        h.Elevator.EnterMaintenance(); // keep it from answering
        h.Hall(0, 3, Direction.Up);
        h.Hall(100, 3, Direction.Up);

        Assert.Single(h.Events.OfType<HallCallRegistered>());
        Assert.Equal(0, Assert.Single(h.Elevator.HallCalls).RegisteredAt.Ms);
    }
}
