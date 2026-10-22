using Elevator.Domain.Events;

namespace Elevator.Domain.Tests;

internal sealed class StubPolicy(Func<ElevatorSnapshot, int?> nextGoal) : ISchedulingPolicy
{
    public string Name => "stub";

    public PolicyDecision Decide(ElevatorSnapshot snapshot) => new(nextGoal(snapshot));
}

internal static class TestPolicies
{
    public static readonly ISchedulingPolicy NearestCarCallThenOldestHallCall = new StubPolicy(s =>
        s.CarCalls.Count > 0 ? s.CarCalls.MinBy(c => Math.Abs(c - s.CurrentFloor))
        : s.HallCalls.Count > 0 ? s.HallCalls[0].Floor
        : null);
}

internal sealed class Harness
{
    public const int Floors = 10;

    public Harness(ISchedulingPolicy? policy = null, int startFloor = 1, ElevatorTiming? timing = null)
    {
        Elevator = new Elevator(0, Floors, timing ?? ElevatorTiming.Default, policy ?? TestPolicies.NearestCarCallThenOldestHallCall, startFloor);
    }

    public Elevator Elevator { get; }

    public List<DomainEvent> Events { get; } = [];

    public void At(long ms)
    {
        Elevator.Tick(new SimTime(ms));
        Events.AddRange(Elevator.DrainEvents());
    }

    public void Hall(long ms, int floor, Direction direction)
    {
        At(ms);
        Elevator.AddHallCall(floor, direction);
        At(ms);
    }

    public void Car(long ms, int floor)
    {
        At(ms);
        Elevator.AddRequest(floor);
        At(ms);
    }

    public IEnumerable<StateChanged> States => Events.OfType<StateChanged>();

    public StateChanged FirstChangeTo(ElevatorState state, int? floor = null) =>
        States.First(e => e.To == state && (floor is null || e.Floor == floor));
}
