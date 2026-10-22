using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Simulation;

namespace Elevator.Runtime.Tests;

public class SupervisionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    public static TheoryData<int, int, Direction, int> InvalidRequests => new()
    {
        { 99, 5, Direction.Down, 1 },
        { 0, 5, Direction.Up, 1 },
        { 5, 21, Direction.Up, 1 },
        { 5, 5, Direction.Up, 1 },
        { 5, 9, Direction.Down, 1 },
        { 5, 9, Direction.Up, 0 },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task An_invalid_request_is_refused_and_the_system_keeps_serving(int pickup, int destination, Direction direction, int load)
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Floors: 20, Elevators: 3, Speed: 50));
        var bad = new Request(pickup, destination, direction, system.Clock.Now, Id: 1, Load: load);

        await Assert.ThrowsAsync<ArgumentException>(() => system.AssignRequest(bad).WaitAsync(Limit));
        await Assert.ThrowsAsync<ArgumentException>(() => system.FindBestElevator(bad).WaitAsync(Limit));

        var good = Request.Create(9, 14, system.Clock.Now, 20, id: 2).Value!;
        Assert.Equal(2, (await system.AssignRequest(good).WaitAsync(Limit)).RequestId);
        Assert.Null(system.Fault);
        Assert.Equal(0, system.InFlight);
        var rejected = Assert.Single(system.Events.OfType<RequestRejected>());
        Assert.Equal(("Request", pickup), (rejected.Kind, rejected.Floor));
    }

    [Fact]
    public async Task A_handler_exception_faults_the_system_instead_of_hanging_callers()
    {
        var engine = SimEngine.Create(Scenario());
        engine.AdvanceTo(new SimTime(10_000));
        var handover = engine.HandOver();
        var handlers = handover.Handlers.Select(h => h is PassengersHandler p ? new ThrowsOn(p, requestId: 666) : h).ToList();

        var system = ElevatorSystem.FromEngine(handover with { Handlers = handlers }, new RuntimeOptions(Floors: 10, Elevators: 1, Speed: 50));
        try
        {
            var doomed = Request.Create(2, 6, system.Clock.Now, 10, id: 666).Value!;
            var thrown = await Assert.ThrowsAsync<ElevatorSystemFaultedException>(() => system.AssignRequest(doomed).WaitAsync(Limit));
            Assert.IsType<InvalidOperationException>(thrown.InnerException);
            Assert.Same(thrown.InnerException, system.Fault);

            var late = Request.Create(3, 7, system.Clock.Now, 10, id: 667).Value!;
            await Assert.ThrowsAsync<ElevatorSystemFaultedException>(() => system.AssignRequest(late).WaitAsync(Limit));
            await Assert.ThrowsAsync<ElevatorSystemFaultedException>(() => system.FindBestElevator(late).WaitAsync(Limit));
            Assert.Equal(0, system.InFlight);
        }
        finally
        {
            await system.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    private static Scenario Scenario() => new("supervision", 1, 10, ElevatorTiming.Default, SimTime.FromSeconds(600),
        [new PassengerSpec(SimTime.Zero, 1, 5)], null);

    private sealed class ThrowsOn(IHandler inner, int requestId) : IHandler
    {
        public Address Address => inner.Address;

        public SimTime? NextWakeUp => inner.NextWakeUp;

        public void Handle(Message message, SimTime now, Outbox outbox)
        {
            if (message is PassengerArrives { Request.Id: var id } && id == requestId)
                throw new InvalidOperationException("handler bug");
            inner.Handle(message, now, outbox);
        }
    }
}
