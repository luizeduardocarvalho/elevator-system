using System.Globalization;
using System.Text;
using Elevator.Metrics;

namespace Elevator.Tui.View;

/// <param name="Title">First line, e.g. scenario · strategy · seed.</param>
/// <param name="ClockStart">Time of day at sim time 0; null labels elapsed hours (0h, 1h…).</param>
/// <param name="Plain">Shade characters only (no colour markup): for redirected output and documents.</param>
public sealed record HeatmapOptions(string Title, TimeSpan? ClockStart = null, bool Plain = false);

/// <summary>
/// A fixed colour scale in seconds: value v is at level = number of thresholds ≤ v, so N thresholds give N + 1 levels,
/// each with a markup colour and a plain glyph. Fixed (not stretched to the run), so maps of different runs compare
/// directly: the same colour always means the same number of seconds.
/// </summary>
/// <param name="Signed">Values are differences: margins print with a sign.</param>
public sealed record HeatScale(double[] Thresholds, string[] Colours, char[] Glyphs, char Empty, bool Signed, string[] Labels)
{
    /// <summary>Waits: [0, 15), [15, 30), [30, 45), [45, 60), [60, ∞). The top level starts at the TUI's "long wait" threshold.</summary>
    public static HeatScale Wait { get; } = new(
        [15, 30, 45, 60],
        ["dodgerblue2", "green3", "gold1", "darkorange", "red1"],
        ['░', '▒', '▓', '█', 'X'],
        '·', Signed: false,
        ["< 15 s", "15–30 s", "30–45 s", "45–60 s", "≥ 60 s"]);

    /// <summary>
    /// Differences A − B, diverging around a neutral band −5 ≤ Δ &lt; 5 s: blue where A waits less, red where it waits
    /// more, in steps of 5, 15 and 30 s. Blank = nobody called (no difference to show, not "no difference").
    /// </summary>
    public static HeatScale Difference { get; } = new(
        [-30, -15, -5, 5, 15, 30],
        ["blue1", "dodgerblue2", "skyblue1", "grey50", "gold1", "darkorange", "red1"],
        ['≡', '=', '-', '~', '+', '*', '#'],
        ' ', Signed: true,
        ["A ≥ 30 s less", "15–30 less", "5–15 less", "within ±5 s", "5–15 more", "15–30 more", "A ≥ 30 s more"]);

    public int Level(double seconds)
    {
        var level = 0;
        while (level < Thresholds.Length && seconds >= Thresholds[level]) level++;
        return level;
    }
}

/// <summary>
/// Draws a heat grid as terminal lines: floors top to bottom (top floor first, as in the building view), one character
/// per time bin, hour labels above, per-floor values on the right, and underneath both a strip and the hourly values for
/// all floors together. Pure: the same grid gives the same lines, byte for byte. Colour mode returns Spectre.Console
/// markup strings (no Spectre types: the renderer prints them); plain mode returns text.
/// </summary>
public static class HeatmapView
{
    /// <summary>The wait scale's boundaries in seconds.</summary>
    public static double[] Thresholds => HeatScale.Wait.Thresholds;

    public static char Empty => HeatScale.Wait.Empty;

    private const int LabelWidth = 4;

    public static int Level(double seconds) => HeatScale.Wait.Level(seconds);

    /// <summary>The wait map of one run.</summary>
    public static IReadOnlyList<string> Render(WaitHeatmap map, HeatmapOptions options)
    {
        var stat = map.Stat == HeatStat.Avg ? "average wait" : "p95 wait";
        return Render(map, HeatScale.Wait, options,
            $"{stat} by origin floor × time of call · {BinText(map.BinMs)} bins · sim seconds",
            $"{map.Passengers} passengers · {map.Boarded} boarded · {map.NotBoarded} never boarded (not in any cell) · ({Empty} nobody called)");
    }

    /// <summary>The difference map of two runs on the same passengers: A − B.</summary>
    public static IReadOnlyList<string> RenderDifference(WaitDiffHeatmap map, string nameA, string nameB, HeatmapOptions options)
    {
        var stat = map.Stat == HeatStat.Avg ? "average wait" : "p95 wait";
        var (better, worse, neutral) = (0, 0, 0);
        for (var floor = 1; floor <= map.Floors; floor++)
            for (var bin = 0; bin < map.Bins; bin++)
            {
                if (map.Cell(floor, bin) is not { } v) continue;
                var level = HeatScale.Difference.Level(v);
                if (level < 3) better++;
                else if (level > 3) worse++;
                else neutral++;
            }
        return Render(map, HeatScale.Difference, options,
            $"Δ {stat} = A − B on the same passengers, by origin floor × time of call · {BinText(map.BinMs)} bins · A = {nameA}, B = {nameB} · blue: A waits less",
            $"{map.Paired} passengers boarded in both runs · {map.Excluded} left out (not boarded in one) · A waits less in {better} cells, more in {worse}, within ±5 s in {neutral} · (blank: nobody called)");
    }

    private static IReadOnlyList<string> Render(IHeatGrid map, HeatScale scale, HeatmapOptions options, string subtitle, string footer)
    {
        var lines = new List<string> { Text(options.Title, options.Plain, "bold"), Text(subtitle, options.Plain, "grey"), "" };

        var hourStarts = HourStarts(map, options.ClockStart);
        lines.Add(Pad("") + " " + HourAxis(map, hourStarts, options.ClockStart));

        for (var floor = map.Floors; floor >= 1; floor--)
        {
            var row = new StringBuilder();
            for (var bin = 0; bin < map.Bins; bin++) row.Append(Cell(map.Cell(floor, bin), scale, options.Plain));
            lines.Add($"{Pad(floor.ToString(CultureInfo.InvariantCulture))}│{row}│ {Seconds(map.FloorStat(floor), scale.Signed)}");
        }

        var strip = new StringBuilder();
        for (var bin = 0; bin < map.Bins; bin++) strip.Append(Cell(map.BinStat(bin), scale, options.Plain));
        lines.Add(Pad("") + "└" + new string('─', map.Bins) + "┘");
        lines.Add($"{Pad("all")}│{strip}│ {Seconds(map.Overall, scale.Signed)}");
        lines.Add(Pad("") + " " + HourValues(map, hourStarts, scale.Signed));
        lines.Add("");
        lines.Add(Legend(scale, options.Plain));
        lines.Add(Text(footer, options.Plain, "grey"));
        return lines;
    }

    /// <summary>Bins where an hour starts (on the clock when there is one, else every elapsed hour), with that hour.</summary>
    private static List<(int Bin, int Hour)> HourStarts(IHeatGrid map, TimeSpan? clockStart)
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

    private static string HourAxis(IHeatGrid map, List<(int Bin, int Hour)> starts, TimeSpan? clockStart)
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
    private static string HourValues(IHeatGrid map, List<(int Bin, int Hour)> starts, bool signed)
    {
        var row = new char[map.Bins + 8];
        Array.Fill(row, ' ');
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i].Bin;
            var to = i + 1 < starts.Count ? starts[i + 1].Bin : map.Bins;
            if (map.RangeStat(from, to) is not { } value) continue;
            var text = Math.Round(value).ToString(signed ? "+0;-0;0" : "0", CultureInfo.InvariantCulture) + "s";
            if (text.Length >= to - from + (i + 1 < starts.Count ? 0 : 8)) continue;
            for (var k = 0; k < text.Length; k++) row[from + k] = text[k];
        }
        return new string(row).TrimEnd();
    }

    private static string Cell(double? seconds, HeatScale scale, bool plain)
    {
        if (seconds is not { } s) return plain ? scale.Empty.ToString() : $"[grey35]{scale.Empty}[/]";
        var level = scale.Level(s);
        return plain ? scale.Glyphs[level].ToString() : $"[{scale.Colours[level]}]█[/]";
    }

    private static string Legend(HeatScale scale, bool plain) =>
        string.Join("   ", scale.Labels.Select((label, level) =>
            (plain ? scale.Glyphs[level].ToString() : $"[{scale.Colours[level]}]█[/]") + " " + label));

    private static string Seconds(double? s, bool signed) =>
        s is { } v ? v.ToString(signed ? "+0.0;-0.0;0.0" : "0.0", CultureInfo.InvariantCulture) + " s" : "–";

    private static string BinText(long binMs) =>
        binMs % 60_000 == 0 ? $"{binMs / 60_000} min" : (binMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";

    private static string Pad(string label) => label.PadLeft(LabelWidth);

    private static string Text(string text, bool plain, string style) =>
        plain ? text : $"[{style}]{text.Replace("[", "[[", StringComparison.Ordinal).Replace("]", "]]", StringComparison.Ordinal)}[/]";
}
