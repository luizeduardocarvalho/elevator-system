using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

public class ElevatorMechanicsTests
{
    [Fact]
    public void Hall_call_at_current_floor_runs_one_full_door_cycle()
    {
        var h = new Harness();
        h.Hall(0, 1, Direction.Up);
        h.At(10_000);

        Assert.Equal(0, h.FirstChangeTo(ElevatorState.DoorOpening).At.Ms);
        Assert.Equal(1_000, h.FirstChangeTo(ElevatorState.DoorOpen).At.Ms);
        Assert.Equal(4_000, h.FirstChangeTo(ElevatorState.DoorClosing).At.Ms);
        Assert.Equal(5_000, h.FirstChangeTo(ElevatorState.Idle).At.Ms);
        var answered = Assert.Single(h.Events.OfType<HallCallAnswered>());
        Assert.Equal((1_000L, 1, Direction.Up), (answered.At.Ms, answered.Floor, answered.Direction));
        Assert.Equal(ElevatorState.Idle, h.Elevator.State);
        Assert.Null(h.Elevator.CommittedDirection);
    }

    [Fact]
    public void Travels_one_floor_per_floor_travel_time_and_stops_at_goal()
    {
        var h = new Harness();
        h.Hall(0, 4, Direction.Down);
        h.At(20_000);

        Assert.Equal(
            [(2_000L, 2), (4_000L, 3), (6_000L, 4)],
            h.Events.OfType<FloorReached>().Select(e => (e.At.Ms, e.Floor)));
        var stop = h.FirstChangeTo(ElevatorState.DoorOpening);
        Assert.Equal((6_000L, 4, ElevatorState.MovingUp), (stop.At.Ms, stop.Floor, stop.From));
        var opened = Assert.Single(h.Events.OfType<DoorsOpened>());
        Assert.Equal(Direction.Down, opened.Lantern);
    }

    [Fact]
    public void Always_stops_for_a_car_call_on_the_way_even_when_policy_goal_is_further()
    {
        var h = new Harness(new StubPolicy(s => s.HallCalls.Count > 0 ? s.HallCalls[0].Floor : s.CarCalls.FirstOrDefault() is var c and > 0 ? c : null));
        h.Hall(0, 8, Direction.Down);
        h.Car(100, 4);
        h.At(60_000);

        var stops = h.Events.OfType<DoorsOpened>().Select(e => e.Floor).ToList();
        Assert.Equal([4, 8], stops);
    }

    [Fact]
    public void Does_not_stop_for_a_hall_call_it_passes_when_it_is_not_the_goal()
    {
        var h = new Harness(new StubPolicy(s => s.HallCalls.Count > 0 ? s.HallCalls[0].Floor : null));
        h.Hall(0, 8, Direction.Up);
        h.Hall(100, 4, Direction.Up);
        h.At(15_000);

        Assert.Contains(h.Events.OfType<FloorReached>(), e => e.Floor == 4);
        Assert.Equal([8], h.Events.OfType<DoorsOpened>().Select(e => e.Floor));
    }

    [Fact]
    public void Only_the_hall_call_matching_the_lantern_is_answered()
    {
        var h = new Harness(new StubPolicy(s => s.HallCalls.Count > 0 ? s.HallCalls[0].Floor : null));
        h.Hall(0, 5, Direction.Up);
        h.Hall(100, 5, Direction.Down);
        h.At(9_500);

        var answered = Assert.Single(h.Events.OfType<HallCallAnswered>());
        Assert.Equal(Direction.Up, answered.Direction);
        Assert.Equal(Direction.Up, h.Elevator.CommittedDirection);
        Assert.Contains(h.Elevator.HallCalls, c => c is { Floor: 5, Direction: Direction.Down });
    }

    [Fact]
    public void Matching_hall_call_while_doors_open_is_answered_immediately_and_restarts_dwell()
    {
        var h = new Harness();
        h.Hall(0, 1, Direction.Up);
        h.Hall(3_000, 1, Direction.Up);
        h.At(10_000);

        Assert.Equal([1_000L, 3_000L], h.Events.OfType<HallCallAnswered>().Select(e => e.At.Ms));
        Assert.Equal(6_000, h.FirstChangeTo(ElevatorState.DoorClosing).At.Ms);
    }

    [Fact]
    public void Matching_hall_call_while_doors_close_reopens_them()
    {
        var h = new Harness();
        h.Hall(0, 1, Direction.Up);
        h.Hall(4_500, 1, Direction.Up);
        h.At(15_000);

        var reopen = Assert.Single(h.States, e => e.Trigger == Trigger.Reopen);
        Assert.Equal((4_500L, ElevatorState.DoorClosing, ElevatorState.DoorOpening), (reopen.At.Ms, reopen.From, reopen.To));
        Assert.Equal([1_000L, 5_000L], h.Events.OfType<HallCallAnswered>().Select(e => e.At.Ms));
    }

    [Fact]
    public void Opposite_hall_call_while_doors_close_does_not_reopen()
    {
        var h = new Harness(new StubPolicy(s => s.HallCalls.Count > 0 ? s.HallCalls[0].Floor : null), startFloor: 5);
        h.Hall(0, 5, Direction.Up);
        h.Hall(4_500, 5, Direction.Down);
        h.At(4_900);

        Assert.DoesNotContain(h.States, e => e.Trigger == Trigger.Reopen);
        Assert.Equal(ElevatorState.DoorClosing, h.Elevator.State);
    }

    [Fact]
    public void Car_call_for_the_floor_it_stands_at_is_ignored()
    {
        var h = new Harness();
        h.Hall(0, 1, Direction.Up);
        h.At(2_000);
        h.Car(2_000, 1);

        Assert.Empty(h.Events.OfType<CarCallRegistered>());
        Assert.Empty(h.Elevator.TargetFloors);
    }

    [Fact]
    public void Car_calls_ahead_are_served_after_boarding()
    {
        var h = new Harness();
        h.Hall(0, 1, Direction.Up);
        h.Car(2_000, 6);
        h.At(30_000);

        var opened = h.Events.OfType<DoorsOpened>().Last();
        Assert.Equal((16_000L, 6), (opened.At.Ms, opened.Floor));
        Assert.Empty(h.Elevator.TargetFloors);
    }

    [Fact]
    public void Maintenance_is_entered_from_idle_and_holds_calls_until_exit()
    {
        var h = new Harness();
        h.Elevator.EnterMaintenance();
        h.Hall(0, 5, Direction.Up);
        h.At(10_000);
        Assert.Equal(ElevatorState.Maintenance, h.Elevator.State);
        Assert.Equal(1, h.Elevator.CurrentFloor);

        h.Elevator.ExitMaintenance();
        Assert.Equal(ElevatorState.MovingUp, h.Elevator.State);
    }

    [Fact]
    public void Time_cannot_go_backwards()
    {
        var h = new Harness();
        h.At(1_000);
        Assert.Throws<ArgumentOutOfRangeException>(() => h.At(999));
    }

    [Fact]
    public void Result_does_not_depend_on_tick_size()
    {
        static List<DomainEvent> Run(long tickMs)
        {
            (long At, int Floor, Direction Direction)[] calls = [(0, 7, Direction.Down), (3_000, 2, Direction.Up), (21_000, 7, Direction.Down)];
            var h = new Harness();
            var next = 0;
            for (long t = 0; t <= 60_000; t += tickMs)
            {
                for (; next < calls.Length && calls[next].At <= t + tickMs; next++)
                    h.Hall(calls[next].At, calls[next].Floor, calls[next].Direction);
                h.At(t + tickMs);
            }
            return h.Events;
        }

        var reference = Run(60_000);
        Assert.Equal(reference, Run(1));
        Assert.Equal(reference, Run(100));
        Assert.Equal(reference, Run(7_777));
    }
}
