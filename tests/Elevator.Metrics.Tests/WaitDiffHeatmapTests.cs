using Elevator.Domain;
using Elevator.Domain.Events;

namespace Elevator.Metrics.Tests;

public class WaitDiffHeatmapTests
{
    private static SimTime S(double seconds) => SimTime.FromSeconds(seconds);

    private static readonly DomainEvent[] Arrivals =
    [
        new SimulationStarted(S(0), "pair", 1, 4, "look", 4),
        new PassengerArrived(S(0), 1, 4, 1),
        new PassengerArrived(S(10), 2, 4, 1),
        new PassengerArrived(S(70), 3, 1, 3),
        new PassengerArrived(S(80), 4, 2, 4),
    ];

    private static readonly DomainEvent[] A =
    [
        .. Arrivals,
        new PassengerBoarded(S(10), 1, 0, 4),
        new PassengerBoarded(S(40), 2, 0, 4),
        new PassengerBoarded(S(75), 3, 1, 1),
    ];

    private static readonly DomainEvent[] B =
    [
        .. Arrivals,
        new PassengerBoarded(S(40), 1, 0, 4),
        new PassengerBoarded(S(30), 2, 0, 4),
        new PassengerBoarded(S(75), 3, 1, 1),
        new PassengerBoarded(S(95), 4, 2, 2),
    ];

    [Fact]
    public void A_cell_is_the_mean_paired_difference_over_the_same_people()
    {
        var map = WaitDiffHeatmap.FromEvents(A, B, binS: 60);
        Assert.Equal((4, 2), (map.Floors, map.Bins));
        Assert.Equal(-10.0, map.Cell(4, 0)!.Value, 6);
        Assert.Equal(0.0, map.Cell(1, 1)!.Value, 6);
        Assert.Equal(2, map.Count(4, 0));
    }

    [Fact]
    public void Only_people_who_boarded_in_both_runs_are_paired()
    {
        var map = WaitDiffHeatmap.FromEvents(A, B, binS: 60);
        Assert.Equal((4, 3, 1), (map.Passengers, map.Paired, map.Excluded));
        Assert.Null(map.Cell(2, 1));
    }

    [Fact]
    public void Margins_pool_the_pairs()
    {
        var map = WaitDiffHeatmap.FromEvents(A, B, binS: 60);
        Assert.Equal(-10.0, map.FloorStat(4)!.Value, 6);
        Assert.Equal(-20.0 / 3, map.Overall!.Value, 6);
        Assert.Equal(-10.0, map.BinStat(0)!.Value, 6);
    }

    [Fact]
    public void P95_is_the_difference_of_the_two_p95s()
    {
        var map = WaitDiffHeatmap.FromEvents(A, B, binS: 60, HeatStat.P95);
        Assert.Equal(-10.0, map.Cell(4, 0)!.Value, 6);
    }

    [Fact]
    public void A_run_against_itself_is_zero_everywhere()
    {
        var map = WaitDiffHeatmap.FromEvents(B, B, binS: 60);
        Assert.Equal(0.0, map.Overall!.Value, 9);
        Assert.Equal(map.Passengers, map.Paired);
    }

    [Fact]
    public void Runs_with_different_passengers_are_refused()
    {
        DomainEvent[] other = [.. Arrivals[..^1], new PassengerArrived(S(81), 4, 2, 4)];
        var error = Assert.Throws<ArgumentException>(() => WaitDiffHeatmap.FromEvents(A, other, binS: 60));
        Assert.Contains("different passengers", error.Message, StringComparison.Ordinal);
    }
}
