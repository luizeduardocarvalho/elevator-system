using Elevator.Domain;
using Elevator.Domain.Events;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Simulation.Tests;

public class EndToEndProperties
{
    private const int FloorCount = 10;

    private static readonly Gen<PassengerSpec> PassengerGen =
        from at in Gen.Choose(0, 300_000)
        from from_ in Gen.Choose(1, FloorCount)
        from offset in Gen.Choose(1, FloorCount - 1)
        select new PassengerSpec(new SimTime(at), from_, (from_ - 1 + offset) % FloorCount + 1);

    private static readonly Gen<(PassengerSpec[] Passengers, long Tick)> RunGen =
        from passengers in Gen.ArrayOf(PassengerGen)
        from tick in Gen.Elements(1L, 100L, 1_000L, 30_000L)
        select (passengers, tick);

    [Property(MaxTest = 150)]
    public Property Every_request_is_served_exactly_once_and_correctly() => Prop.ForAll(RunGen.ToArbitrary(), input =>
    {
        var scenario = new Scenario("prop", 1, FloorCount, ElevatorTiming.Default,
            SimTime.FromSeconds(100_000), input.Passengers, null);
        var result = ScenarioRunner.Run(scenario, tickMs: input.Tick);
        var passengers = ScenarioRunner.BuildPassengers(scenario).ToDictionary(p => p.Id);

        Assert.Equal((passengers.Count, 0), (result.Delivered, result.Unserved));

        var boarded = result.Events.OfType<PassengerBoarded>().ToList();
        var alighted = result.Events.OfType<PassengerAlighted>().ToList();
        Assert.Equal(passengers.Keys.Order(), boarded.Select(b => b.PassengerId).Order());
        Assert.Equal(passengers.Keys.Order(), alighted.Select(a => a.PassengerId).Order());

        var answers = result.Events.OfType<HallCallAnswered>().ToList();
        foreach (var b in boarded)
        {
            var p = passengers[b.PassengerId];
            Assert.Equal(p.From, b.Floor);
            Assert.True(b.At >= p.ArrivesAt);
            Assert.Contains(answers, a => a.At == b.At && a.Floor == p.From && a.Direction == p.Direction);
        }
        foreach (var a in alighted)
        {
            Assert.Equal(passengers[a.PassengerId].To, a.Floor);
            Assert.True(a.At > boarded.Single(b => b.PassengerId == a.PassengerId).At);
        }
    });
}
