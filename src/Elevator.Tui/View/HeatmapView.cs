using System.Globalization;
using System.Text;
using Elevator.Metrics;

namespace Elevator.Tui.View;

/// <param name="Title">First line, e.g. scenario · strategy · seed.</param>
/// <param name="ClockStart">Time of day at sim time 0; null labels elapsed hours (0h, 1h…).</param>
/// <param name="Plain">Shade characters only (no colour markup): for redirected output and documents.</param>
public sealed record HeatmapOptions(string Title, TimeSpan? ClockStart = null, bool Plain = false);

/// <summary>
/// Draws a <see cref="WaitHeatmap"/> as terminal lines: floors top to bottom (top floor first, as in the building
/// view), one character per time bin, hour labels above, per-floor values on the right, and underneath both a strip
/// and the hourly values for all floors together. Pure: the same map gives the same lines, byte for byte.
/// <para>
/// The colour scale is fixed in seconds (not stretched to the run), so heatmaps of different strategies or seeds
/// can be compared side by side: the same colour always means the same wait. Its top level starts at 60 s, the TUI's
/// "long wait" threshold.
/// </para>
/// Colour mode returns Spectre.Console markup strings (no Spectre types: the renderer prints them); plain mode returns text.
/// </summary>
public static class HeatmapView
{
    /// <summary>Level boundaries in seconds: [0, 15), [15, 30), [30, 45), [45, 60), [60, ∞).</summary>
    public static readonly double[] Thresholds = [15, 30, 45, 60];

    /// <summary>One colour and one plain glyph per level.</summary>
    private static readonly string[] Colours = ["dodgerblue2", "green3", "gold1", "darkorange", "red1"];

    private static readonly char[] Glyphs = ['░', '▒', '▓', '█', 'X'];

    public const char Empty = '·';

    private const int LabelWidth = 4;

    public static int Level(double seconds)
    {
        var level = 0;
        while (level < Thresholds.Length && seconds >= Thresholds[level]) level++;
        return level;
    }

    public static IReadOnlyList<string> Render(WaitHeatmap map, HeatmapOptions options)
    {
        var lines = new List<string> { Text(options.Title, options.Plain, "bold") };
        var statName = map.Stat == HeatStat.Avg ? "average wait" : "p95 wait";
        lines.Add(Text($"{statName} by origin floor × time of call · {BinText(map.BinMs)} bins · sim seconds", options.Plain, "grey"));
        lines.Add("");

        var hourStarts = HourStarts(map, options.ClockStart);
        lines.Add(Pad("") + " " + HourAxis(map, hourStarts, options.ClockStart));

        for (var floor = map.Floors; floor >= 1; floor--)
        {
            var row = new StringBuilder();
            for (var bin = 0; bin < map.Bins; bin++) row.Append(Cell(map.Cell(floor, bin), options.Plain));
            lines.Add($"{Pad(floor.ToString(CultureInfo.InvariantCulture))}│{row}│ {Seconds(map.FloorStat(floor))}");
        }

        var strip = new StringBuilder();
        for (var bin = 0; bin < map.Bins; bin++) strip.Append(Cell(map.BinStat(bin), options.Plain));
        lines.Add(Pad("") + "└" + new string('─', map.Bins) + "┘");
        lines.Add($"{Pad("all")}│{strip}│ {Seconds(map.Overall)}");
        lines.Add(Pad("") + " " + HourValues(map, hourStarts));
        lines.Add("");
        lines.Add(Legend(options.Plain));
        lines.Add(Text($"{map.Passengers} passengers · {map.Boarded} boarded · {map.NotBoarded} never boarded (not in any cell) · ({Empty} nobody called)",
            options.Plain, "grey"));
        return lines;
    }

    /// <summary>Bins where an hour starts (on the clock when there is one, else every elapsed hour), with that hour.</summary>
    private static List<(int Bin, int Hour)> HourStarts(WaitHeatmap map, TimeSpan? clockStart)
    {
        var offsetMs = (long)(clockStart?.TotalMilliseconds ?? 0);
        var result = new List<(int, int)>();
        var lastHour = -1L;
        for (var bin = 0; bin < map.Bins; bin++)
        {
            var hour = (offsetMs + bin * map.BinMs) / 3_600_000;
            if (hour != lastHour && (offsetMs + bin * map.BinMs) % 3_600_000 < map.BinMs) result.Add((bin, (int)hour));
            lastHour = hour;
        }
        return result;
    }

    private static string HourAxis(WaitHeatmap map, List<(int Bin, int Hour)> starts, TimeSpan? clockStart)
    {
        var axis = new char[map.Bins + 8];
        Array.Fill(axis, ' ');
        foreach (var (bin, hour) in starts)
        {
            var label = clockStart is null ? $"{hour}h" : (hour % 24).ToString("00", CultureInfo.InvariantCulture);
            if (bin > 0 && axis[bin - 1] != ' ') continue; // would touch the previous label
            for (var i = 0; i < label.Length && bin + i < axis.Length; i++) axis[bin + i] = label[i];
        }
        return new string(axis).TrimEnd();
    }

    /// <summary>Under each hour, that hour's value for all floors, in whole seconds when it fits before the next hour.</summary>
    private static string HourValues(WaitHeatmap map, List<(int Bin, int Hour)> starts)
    {
        var row = new char[map.Bins + 8];
        Array.Fill(row, ' ');
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i].Bin;
            var to = i + 1 < starts.Count ? starts[i + 1].Bin : map.Bins;
            if (map.RangeStat(from, to) is not { } value) continue;
            var text = Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "s";
            if (text.Length >= to - from + (i + 1 < starts.Count ? 0 : 8)) continue;
            for (var k = 0; k < text.Length; k++) row[from + k] = text[k];
        }
        return new string(row).TrimEnd();
    }

    private static string Cell(double? seconds, bool plain)
    {
        if (seconds is not { } s) return plain ? Empty.ToString() : $"[grey35]{Empty}[/]";
        var level = Level(s);
        return plain ? Glyphs[level].ToString() : $"[{Colours[level]}]█[/]";
    }

    private static string Legend(bool plain)
    {
        var parts = new List<string>();
        for (var level = 0; level <= Thresholds.Length; level++)
        {
            var range = level == 0 ? $"< {Thresholds[0]:0} s"
                : level == Thresholds.Length ? $"≥ {Thresholds[^1]:0} s"
                : $"{Thresholds[level - 1]:0}–{Thresholds[level]:0} s";
            parts.Add((plain ? Glyphs[level].ToString() : $"[{Colours[level]}]█[/]") + " " + range);
        }
        return string.Join("   ", parts);
    }

    private static string Seconds(double? s) => s is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "–";

    private static string BinText(long binMs) =>
        binMs % 60_000 == 0 ? $"{binMs / 60_000} min" : (binMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";

    private static string Pad(string label) => label.PadLeft(LabelWidth);

    private static string Text(string text, bool plain, string style) =>
        plain ? text : $"[{style}]{text.Replace("[", "[[", StringComparison.Ordinal).Replace("]", "]]", StringComparison.Ordinal)}[/]";
}
