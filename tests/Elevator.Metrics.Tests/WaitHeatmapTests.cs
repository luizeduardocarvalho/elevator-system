using Elevator.Domain;
using Elevator.Domain.Events;

namespace Elevator.Metrics.Tests;

public class WaitHeatmapTests
{
    private static SimTime S(double seconds) => SimTime.FromSeconds(seconds);

    /// <summary>
    /// 5 floors, 60 s bins. Floor 1: two callers in bin 0 (waits 10 s and 30 s), one in bin 1 (wait 50 s).
    /// Floor 4: one caller in bin 1 (wait 20 s), one who never boards. Bin 2 has a caller on floor 3 (wait 5 s).
    /// </summary>
    private static readonly DomainEvent[] Log =
    [
        new SimulationStarted(S(0), "synthetic", 1, 5, "look", 6),
        new PassengerArrived(S(0), 1, 1, 5),
        new PassengerArrived(S(59.9), 2, 1, 3),
        new PassengerArrived(S(60), 3, 1, 4),       // exactly on the boundary: bin 1
        new PassengerArrived(S(70), 4, 4, 1),
        new PassengerArrived(S(90), 5, 4, 2),       // never boards
        new PassengerArrived(S(130), 6, 3, 1),
        new PassengerBoarded(S(10), 1, 0, 1),
        new PassengerBoarded(S(89.9), 2, 0, 1),
        new PassengerBoarded(S(110), 3, 0, 1),
        new PassengerBoarded(S(90), 4, 1, 4),
        new PassengerBoarded(S(135), 6, 1, 3),
        new SimulationEnded(S(200), 5, 1),
    ];

    [Fact]
    public void Cells_are_the_mean_wait_by_origin_floor_and_call_time_bin()
    {
        var map = WaitHeatmap.FromEvents(Log, binS: 60);

        Assert.Equal((5, 3, 60_000L), (map.Floors, map.Bins, map.BinMs));
        Assert.Equal(20.0, map.Cell(1, 0)!.Value, 6);   // (10 + 30) / 2
        Assert.Equal(50.0, map.Cell(1, 1)!.Value, 6);
        Assert.Equal(20.0, map.Cell(4, 1)!.Value, 6);
        Assert.Equal(5.0, map.Cell(3, 2)!.Value, 6);
        Assert.Equal((2, 1, 1), (map.Count(1, 0), map.Count(1, 1), map.Count(4, 1)));
    }

    [Fact]
    public void Empty_cells_have_no_value_not_zero()
    {
        var map = WaitHeatmap.FromEvents(Log, binS: 60);
        Assert.Null(map.Cell(2, 0));
        Assert.Null(map.Cell(5, 2));
        Assert.Equal(0, map.Count(2, 0));
        Assert.Null(map.FloorStat(5));
    }

    [Fact]
    public void Passengers_who_never_boarded_are_counted_apart_and_excluded_from_the_cells()
    {
        var map = WaitHeatmap.FromEvents(Log, binS: 60);
        Assert.Equal((6, 5, 1), (map.Passengers, map.Boarded, map.NotBoarded));
        Assert.Equal(1, map.Count(4, 1));
    }

    [Fact]
    public void Margins_pool_passengers_rather_than_averaging_cells()
    {
        var map = WaitHeatmap.FromEvents(Log, binS: 60);
        Assert.Equal(30.0, map.FloorStat(1)!.Value, 6);          // (10 + 30 + 50) / 3, not (20 + 50) / 2
        Assert.Equal(35.0, map.BinStat(1)!.Value, 6);            // (50 + 20) / 2
        Assert.Equal(23.0, map.RangeStat(0, 3)!.Value, 6);       // all five boarded: 115 / 5
        Assert.Equal(map.Overall, map.RangeStat(0, map.Bins));
        Assert.Null(map.RangeStat(3, 3));
    }

    [Fact]
    public void P95_uses_the_nearest_rank_like_the_report()
    {
        var map = WaitHeatmap.FromEvents(Log, binS: 60, HeatStat.P95);
        Assert.Equal(30.0, map.Cell(1, 0)!.Value, 6);
        Assert.Equal(50.0, map.FloorStat(1)!.Value, 6);
    }

    [Fact]
    public void Bins_cover_the_calls_and_can_be_extended_to_a_span()
    {
        Assert.Equal(14, WaitHeatmap.FromEvents(Log, binS: 10).Bins);   // last call at 130 s → bins 0..13
        Assert.Equal(10, WaitHeatmap.FromEvents(Log, binS: 60, spanS: 600).Bins);
    }

    [Fact]
    public void A_log_without_passengers_gives_an_empty_map()
    {
        var map = WaitHeatmap.FromEvents([new SimulationStarted(S(0), "empty", 1, 4, "look", 0), new SimulationEnded(S(1), 0, 0)], binS: 60);
        Assert.Equal((4, 0, 0), (map.Floors, map.Bins, map.Passengers));
        Assert.Null(map.Overall);
    }
}
