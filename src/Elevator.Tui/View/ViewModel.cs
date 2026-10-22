using System.Globalization;
using System.Text.Json;
using Elevator.Domain;
using Elevator.Metrics;
using Elevator.Tui.State;

namespace Elevator.Tui.View;

/// <summary>
/// The pure view model (ADR-011): <c>BuildingState → ScreenModel</c>. No I/O, no Spectre, no engine; the same state
/// and options always give the same model.
/// </summary>
public static class ViewModel
{
    /// <summary>Columns of the right-hand panel (metrics and ops); hidden below <see cref="SidePanelMinWidth"/>.</summary>
    public const int SidePanelWidth = 48;
    public const int SidePanelMinWidth = 100;

    /// <summary>Below this the screen only says so (Spectre's layout needs room for its regions).</summary>
    public const int MinWidth = 40, MinHeight = 12;

    private const int ShaftCellWidth = 11;
    private const int CompactShaftCellWidth = 6; // 3 columns of glyph, padding and the border
    private const int SidePanelChromeRows = 7;  // per side panel: borders, summary or totals, table header
    private const int FloorAndWaitingWidth = 16;
    private const int FixedRows = 2;
    private const int GridChromeRows = 4;
    private const int TailChromeRows = 2;
    private const int MinTailLines = 3;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ScreenModel Build(BuildingState state, ViewOptions options)
    {
        var status = options.Status ?? new StatusInfo();
        var showSide = options.Width >= SidePanelMinWidth;

        var maxFloorRows = Math.Max(1, options.Height - FixedRows - (MinTailLines + TailChromeRows) - GridChromeRows);
        var floorRows = Math.Min(state.Floors, maxFloorRows);

        // A wide fleet works like a tall building: what does not fit is a window around the selected elevator, which
        // ←/→ move. Shafts first go compact; past that, the window keeps a column at each edge for the ◀n / n▶ counts.
        // The side panels list one row per elevator each, so they get the window that fits beside the building.
        var n = state.Elevators.Count;
        var shaftsWidth = options.Width - FloorAndWaitingWidth - (showSide ? SidePanelWidth : 0);
        var compact = shaftsWidth < n * ShaftCellWidth;
        var shafts = compact && shaftsWidth < n * CompactShaftCellWidth
            ? Window(n, (shaftsWidth - 2 * CompactShaftCellWidth) / CompactShaftCellWidth, options.Selected)
            : (First: 0, Count: n);
        var sideBudget = options.Height - FixedRows - (MinTailLines + TailChromeRows);
        var listed = Window(n, (sideBudget - 2 * SidePanelChromeRows) / 2, options.Selected);
        var range = listed.Count == n ? null
            : listed.Count == 1 ? $"E{listed.First} of {n}"
            : $"E{listed.First}–E{listed.First + listed.Count - 1} of {n}";
        var gridRows = floorRows + GridChromeRows;
        var sideRows = showSide ? 2 * (listed.Count + SidePanelChromeRows) : 0;
        var mainRows = Math.Min(Math.Max(gridRows, sideRows), Math.Max(gridRows, options.Height - FixedRows - TailChromeRows));
        var tailLines = Math.Max(0, options.Height - FixedRows - TailChromeRows - mainRows);

        return new ScreenModel(
            BuildStatus(state, status, options.Selected),
            BuildGrid(state, options.Selected, floorRows, compact, shafts),
            BuildMetrics(state, options.Selected, listed) with { Range = range },
            BuildOps(state, listed) with { Range = range },
            BuildTail(state, tailLines),
            status.ReadOnly
                ? "space pause · n step · +/- speed · [ ] seek ∓60 s · 0 restart · ←/→ select · q quit"
                : "space pause · n step · +/- speed · ←/→ select · m maint · e emergency · s stuck 60 s · [ ] seek ∓60 s · q quit",
            showSide,
            options.Width < MinWidth || options.Height < MinHeight)
        {
            MainRows = mainRows,
        };
    }

    private static StatusBar BuildStatus(BuildingState s, StatusInfo info, int selected) => new(
        info.Source,
        $"{s.Scenario} · {s.Scheduling}+{s.Dispatch} · seed {s.Seed}",
        Clock(s.Now),
        info.TargetSpeed,
        info.Achieved is { } a ? a.ToString("0.0", Inv) + "×" : "–",
        info.Paused,
        s.Ended,
        info.ReadOnly ? "read-only" : "faults",
        $"E{selected}",
        info.Message);

    /// <summary>Sim time as h:mm:ss.f.</summary>
    public static string Clock(Instant t)
    {
        var ms = t.Ms;
        return string.Create(Inv, $"{ms / 3_600_000}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms / 100 % 10}");
    }

    /// <summary>At most <paramref name="size"/> (at least 1) of <paramref name="n"/> elevators, centred on <paramref name="selected"/>.</summary>
    private static (int First, int Count) Window(int n, int size, int selected)
    {
        var count = Math.Clamp(size, Math.Min(1, n), n);
        return (Math.Clamp(selected - count / 2, 0, n - count), count);
    }

    private static BuildingGrid BuildGrid(BuildingState s, int selected, int floorRows, bool compact, (int First, int Count) shown)
    {
        var focus = selected < s.Elevators.Count ? s.Elevators[selected].Floor : Floors.Lowest;
        var top = Math.Clamp(focus + floorRows / 2, Math.Min(s.Floors, floorRows), s.Floors);
        var bottom = top - floorRows + 1;

        var rows = new List<FloorRow>(floorRows);
        for (var floor = top; floor >= bottom; floor--)
        {
            var shafts = new ShaftCell[shown.Count];
            for (var j = 0; j < shafts.Length; j++)
            {
                var i = shown.First + j;
                var e = s.Elevators[i];
                shafts[j] = e.Floor == floor ? Cell(e, e.Capacity ?? s.Capacity, compact, i == selected) : ShaftCell.Empty;
            }
            rows.Add(new FloorRow(floor,
                s.Waiting.GetValueOrDefault((floor, Direction.Up)),
                s.Waiting.GetValueOrDefault((floor, Direction.Down)),
                shafts));
        }
        return new BuildingGrid([.. s.Elevators.Skip(shown.First).Take(shown.Count).Select(e => $"E{e.Id}")], rows, compact,
            s.Floors - top, bottom - Floors.Lowest, shown.First, s.Elevators.Count - shown.First - shown.Count);
    }

    private static ShaftCell Cell(ElevatorView e, int capacity, bool compact, bool selected)
    {
        var (glyph, kind) = e.State switch
        {
            ElevatorState.MovingUp => ("▲", CellKind.Moving),
            ElevatorState.MovingDown => ("▼", CellKind.Moving),
            ElevatorState.DoorOpening => ("◧", CellKind.Doors),
            ElevatorState.DoorOpen => ("□", CellKind.Doors),
            ElevatorState.DoorClosing => ("◨", CellKind.Doors),
            ElevatorState.Maintenance => ("✖", CellKind.OutOfService),
            _ => ("■", CellKind.Idle),
        };
        var lantern = e.State is ElevatorState.DoorOpen or ElevatorState.DoorClosing ? Arrow(e.ShownDirection) : "";
        var marker = Marker(e.Mode);
        var letter = MarkerLetter(marker);
        var text = compact
            ? glyph + lantern + letter
            : $"{glyph}{lantern} {Load(e.Riders, capacity)}{(letter.Length > 0 ? " " + letter : "")}";
        return new ShaftCell(text, kind, marker, selected);
    }

    private static string Arrow(Direction? d) => d switch { Direction.Up => "↑", Direction.Down => "↓", _ => "" };

    private static string Load(int riders, int capacity) =>
        capacity == int.MaxValue ? riders.ToString(Inv) : string.Create(Inv, $"{riders}/{capacity}");

    public static ModeMarker Marker(ElevatorMode mode) => mode switch
    {
        ElevatorMode.Draining => ModeMarker.Draining,
        ElevatorMode.Maintenance => ModeMarker.Maintenance,
        ElevatorMode.Emergency => ModeMarker.Emergency,
        ElevatorMode.Fault => ModeMarker.Fault,
        _ => ModeMarker.None,
    };

    public static string MarkerLetter(ModeMarker m) => m switch
    {
        ModeMarker.Draining => "D",
        ModeMarker.Maintenance => "M",
        ModeMarker.Emergency => "E",
        ModeMarker.Fault => "F",
        _ => "",
    };

    private static MetricsPanel BuildMetrics(BuildingState s, int selected, (int First, int Count) listed)
    {
        var waits = s.Waits;
        string avg = "–", p95 = "–", max = "–", longWaits = "–";
        if (waits.Count > 0)
        {
            double sum = 0, top = 0;
            var longCount = 0;
            foreach (var w in waits)
            {
                sum += w;
                top = Math.Max(top, w);
                if (w > BuildingState.LongWaitS) longCount++;
            }
            avg = Seconds1(sum / waits.Count);
            p95 = Seconds1(Percentile.NearestRank(waits, 95)!.Value);
            max = Seconds1(top);
            longWaits = string.Create(Inv, $"{100.0 * longCount / waits.Count:0.0} % ({longCount})");
        }

        var waiting = s.Waiting.Values.Sum();
        var active = s.Arrived - s.Delivered;
        var rows = s.Elevators.Skip(listed.First).Take(listed.Count).Select(e => new ElevatorRow(
            e.TypeName == "standard" ? $"E{e.Id}" : $"E{e.Id} {e.TypeName}",
            JsonNamingPolicy.SnakeCaseUpper.ConvertName(e.State.ToString()),
            Load(e.Riders, e.Capacity ?? s.Capacity),
            string.Create(Inv, $"{Math.Round(100 * e.Utilization(s.Now)):0} %"),
            Marker(e.Mode),
            e.Id == selected)).ToList();

        return new MetricsPanel(avg, p95, max, longWaits,
            string.Create(Inv, $"{s.Delivered} / {s.ExpectedPassengers}"),
            string.Create(Inv, $"{active} ({waiting} waiting, {active - waiting} riding)"),
            rows);
    }

    private static OpsPanel BuildOps(BuildingState s, (int First, int Count) listed)
    {
        long emergencyMs = 0, faultMs = 0;
        int stops = 0, faults = 0;
        var rows = new List<OpsRow>(listed.Count);
        for (var i = 0; i < s.Elevators.Count; i++)
        {
            var e = s.Elevators[i];
            var em = e.ModeMs(ElevatorMode.Emergency, s.Now);
            var fm = e.ModeMs(ElevatorMode.Fault, s.Now);
            emergencyMs += em;
            faultMs += fm;
            stops += e.EmergencyStops;
            faults += e.Faults;
            if (i < listed.First || i >= listed.First + listed.Count) continue;
            rows.Add(new OpsRow($"E{e.Id}", Seconds0(e.ModeMs(ElevatorMode.Maintenance, s.Now)),
                string.Create(Inv, $"{e.EmergencyStops} ({Seconds0(em)})"),
                string.Create(Inv, $"{e.Faults} ({Seconds0(fm)})")));
        }
        return new OpsPanel(rows,
            string.Create(Inv, $"{stops} ({Seconds0(emergencyMs)})"),
            string.Create(Inv, $"{faults} ({Seconds0(faultMs)})"),
            string.Create(Inv, $"{s.Reassignments} (ops {s.OpsReassignments}, +{Seconds0(s.OpsPenaltyMs)})"),
            s.CommandsApplied.ToString(Inv),
            s.AccessDenials.ToString(Inv));
    }

    private static string Seconds1(double s) => s.ToString("0.0", Inv);

    private static string Seconds0(long ms) => (ms / 1000.0).ToString("0", Inv) + " s";

    private static List<TailLine> BuildTail(BuildingState s, int lines)
    {
        var tail = s.Tail;
        var start = Math.Max(0, tail.Count - lines);
        var result = new List<TailLine>(tail.Count - start);
        for (var i = start; i < tail.Count; i++) result.Add(EventLine.Format(tail[i]));
        return result;
    }
}
