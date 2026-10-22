using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Metrics;
using Elevator.Simulation;
using Elevator.Tui.View;

namespace Elevator.Tui.Tests;

public class HeatmapViewTests
{
    private static SimTime S(double seconds) => SimTime.FromSeconds(seconds);

    /// <summary>3 floors, 10-minute bins over 2 hours. Floor 3 waits 70 s at 0:00; floor 1 waits 20 s at 1:00.</summary>
    private static WaitHeatmap Small() => WaitHeatmap.FromEvents(
    [
        new SimulationStarted(S(0), "small", 1, 3, "look", 2),
        new PassengerArrived(S(0), 1, 3, 1),
        new PassengerArrived(S(3600), 2, 1, 2),
        new PassengerBoarded(S(70), 1, 0, 3),
        new PassengerBoarded(S(3620), 2, 0, 1),
        new SimulationEnded(S(7200), 2, 0),
    ], binS: 600, spanS: 7200);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(14.99, 0)]
    [InlineData(15, 1)]
    [InlineData(44.9, 2)]
    [InlineData(59.9, 3)]
    [InlineData(60, 4)]
    [InlineData(900, 4)]
    public void The_colour_scale_is_fixed_in_seconds(double seconds, int level) => Assert.Equal(level, HeatmapView.Level(seconds));

    [Fact]
    public void Plain_rows_run_top_floor_first_with_one_glyph_per_bin_and_the_floor_value_on_the_right()
    {
        var lines = HeatmapView.Render(Small(), new HeatmapOptions("small", new TimeSpan(7, 0, 0), Plain: true));
        var rows = lines.Where(l => l.Contains('│')).ToList();

        Assert.Equal("   3│X···········│ 70.0 s", rows[0]);
        Assert.Equal("   2│············│ –", rows[1]);
        Assert.Equal("   1│······▒·····│ 20.0 s", rows[2]);
        Assert.Equal(" all│X·····▒·····│ 45.0 s", rows[3]);
    }

    [Fact]
    public void Hour_labels_follow_the_clock_start_or_count_elapsed_hours()
    {
        var clock = HeatmapView.Render(Small(), new HeatmapOptions("small", new TimeSpan(7, 0, 0), Plain: true));
        Assert.Contains("     07    08", clock);

        var elapsed = HeatmapView.Render(Small(), new HeatmapOptions("small", Plain: true));
        Assert.Contains("     0h    1h", elapsed);

        // 07:30 start: the first full hour (08) begins three bins in.
        var half = HeatmapView.Render(Small(), new HeatmapOptions("small", new TimeSpan(7, 30, 0), Plain: true));
        Assert.Contains("        08    09", half);
    }

    [Fact]
    public void Hourly_values_sit_under_their_hour()
    {
        var lines = HeatmapView.Render(Small(), new HeatmapOptions("small", new TimeSpan(7, 0, 0), Plain: true));
        Assert.Contains("     70s   20s", lines);
    }

    [Fact]
    public void The_footer_counts_who_is_not_in_any_cell()
    {
        var lines = HeatmapView.Render(Small(), new HeatmapOptions("small", Plain: true));
        Assert.Contains(lines, l => l.StartsWith("2 passengers · 2 boarded · 0 never boarded", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("X ≥ 60 s", StringComparison.Ordinal));
    }

    [Fact]
    public void Colour_lines_are_valid_markup_and_plain_lines_have_none()
    {
        var map = Small();
        foreach (var line in HeatmapView.Render(map, new HeatmapOptions("a [bracketed] title", Plain: false)))
            _ = new Spectre.Console.Markup(line); // throws on malformed markup
        Assert.DoesNotContain(HeatmapView.Render(map, new HeatmapOptions("small", Plain: true)), l => l.Contains("[/]", StringComparison.Ordinal));
    }

    /// <summary>
    /// The working day under LOOK + Cost: the evening down-peak strands people on the upper floors and the morning
    /// up-peak warms the lobby, each compared with a quiet stretch of the same day.
    /// </summary>
    [Fact]
    public void The_working_day_shows_its_peaks_where_they_happen()
    {
        var events = SimEngine.Run(Repo.Load("day.yaml")).Events;
        var map = WaitHeatmap.FromEvents(events, binS: 600);
        static int Bin(double hour) => (int)Math.Round((hour - 7) * 6);

        var eveningUpper = map.RegionStat(2, 20, Bin(17.17), Bin(18))!.Value;
        var afternoonUpper = map.RegionStat(2, 20, Bin(14), Bin(16))!.Value;
        Assert.True(eveningUpper > afternoonUpper + 5, $"evening upper floors {eveningUpper:0.0} s vs afternoon {afternoonUpper:0.0} s");

        var morningLobby = map.RegionStat(1, 1, Bin(8.17), Bin(9))!.Value;
        var lateMorningLobby = map.RegionStat(1, 1, Bin(10), Bin(11.5))!.Value;
        Assert.True(morningLobby > lateMorningLobby, $"morning lobby {morningLobby:0.0} s vs late morning {lateMorningLobby:0.0} s");

        Assert.Equal(map.Passengers, map.Boarded); // everyone served by the end of the day
        Assert.Equal(HeatmapView.Render(map, new HeatmapOptions("day", Plain: true)), HeatmapView.Render(map, new HeatmapOptions("day", Plain: true)));
    }
}
