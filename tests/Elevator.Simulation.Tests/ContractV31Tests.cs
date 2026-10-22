using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Metrics;

namespace Elevator.Simulation.Tests;

public class ContractV31Tests
{
    [Fact]
    public void A_typed_fleet_and_passenger_attributes_are_written_to_the_log()
    {
        var log = SimEngine.Run(Repo.Load("fleet.yaml")).Events;

        var started = Assert.IsType<RunStarted>(log[0]);
        Assert.Equal(["local", "local", "express", "freight"], started.Fleet!.Select(f => f.Type));
        Assert.Equal([10, 10, 10, 20], started.Fleet!.Select(f => f.Capacity));

        var arrivals = log.OfType<PassengerArrived>().ToList();
        Assert.Contains(arrivals, a => a is { From: 1, To: 9, Load: 15 });
        Assert.Contains(arrivals, a => a is { From: 1, To: 17, Vip: true });
        Assert.Contains(arrivals, a => a is { From: 1, To: 20, AccessLevel: 1 });
        Assert.True(arrivals.Count(a => a.Load is null && a.Vip is null && a.AccessLevel is null) > 100);
    }

    [Fact]
    public void The_default_building_writes_none_of_the_new_fields()
    {
        var jsonl = Repo.ToJsonl(SimEngine.Run(Repo.Load("lunch.yaml")));
        Assert.DoesNotContain("\"fleet\"", jsonl, StringComparison.Ordinal);
        Assert.DoesNotContain("\"vip\"", jsonl, StringComparison.Ordinal);
        Assert.DoesNotContain("\"load\"", jsonl, StringComparison.Ordinal);
        Assert.DoesNotContain("\"accessLevel\"", jsonl, StringComparison.Ordinal);
    }

    [Fact]
    public void Bypassed_full_uses_each_elevators_own_capacity_when_the_log_has_a_fleet()
    {
        static IReadOnlyList<PassengerRecord> Analyze(IReadOnlyList<FleetEntry>? fleet) => PassengerLifecycle.Analyze(
        [
            new RunStarted(Instant.Zero, "unit", 1, 10, "look", 2, 1, "cost", 10, 0.8, "next-trip", fleet),
            new PassengerArrived(Instant.Zero, 2, 1, 5, Load: 10),
            new AssignmentAccepted(Instant.Zero, 2, 0),
            new PassengerBoarded(Instant.Zero, 2, 0, 1),
            new PassengerArrived(Instant.Zero, 1, 3, 9),
            new AssignmentAccepted(Instant.Zero, 1, 0),
            new PassengerBoarded(new Instant(10_000), 1, 0, 3),
        ]);

        var building = Analyze(null).Single(r => r.Id == 1);
        var own = Analyze([new FleetEntry(0, "freight", 20, 0.8, ulong.MaxValue)]).Single(r => r.Id == 1);
        Assert.Equal(10.0, building.WaitBuckets[WaitBucket.BypassedFull]);
        Assert.Equal(0.0, own.WaitBuckets[WaitBucket.BypassedFull]);
        Assert.Equal(10.0, own.WaitS!.Value);
    }

    [Fact]
    public void Reports_split_vip_and_other_waits_only_when_there_are_vips()
    {
        var fleet = ReportBuilder.Build(SimEngine.Run(Repo.Load("fleet.yaml")).Events);
        Assert.Equal(1, fleet.VipWait!.Count);
        Assert.Equal(fleet.Delivered - 1, fleet.NonVipWait!.Count);

        var plain = ReportBuilder.Build(SimEngine.Run(Repo.Load("lunch.yaml")).Events);
        Assert.Null(plain.VipWait);
        Assert.Null(plain.NonVipWait);
    }
}
