using System.Globalization;
using System.Text.Json;
using Elevator.Domain;
using Elevator.Metrics;
using Elevator.Tui.State;

namespace Elevator.Tui.View;

/// <summary>
/// The pure view model (ADR-011): <c>BuildingState → ScreenModel</c>. No I/O, no Spectre, no engine; the same state
/// and options always give the same model, which is what lets replay be compared to live screen by screen.
/// </summary>
public static class ViewModel
{
    /// <summary>Columns of the right-hand panel (metrics and ops); hidden below <see cref="SidePanelMinWidth"/>.</summary>
    public const int SidePanelWidth = 48;
    public const int SidePanelMinWidth = 100;

    private const int ShaftCellWidth = 11; // "□↑ 10/10 M" plus a separator
    private const int FloorAndWaitingWidth = 16;
    private const int FixedRows = 2;       // status bar + help line
    private const int GridChromeRows = 4;  // panel border + table header
    private const int TailChromeRows = 2;  // panel border
    private const int MinTailLines = 3;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ScreenModel Build(BuildingState state, ViewOptions options)
    {
        var status = options.Status ?? new StatusInfo();
        var showSide = options.Width >= SidePanelMinWidth;

        // Height budget: the building gets every floor it can while the tail keeps at least MinTailLines; the
        // tail takes whatever is left over.
        var maxFloorRows = Math.Max(1, options.Height - FixedRows - (MinTailLines + TailChromeRows) - GridChromeRows);
        var floorRows = Math.Min(state.Floors, maxFloorRows);
        var tailLines = Math.Max(0, options.Height - FixedRows - TailChromeRows - GridChromeRows - floorRows);

        var compact = options.Width < FloorAndWaitingWidth + state.Elevators.Count * ShaftCellWidth + (showSide ? SidePanelWidth : 0);

        return new ScreenModel(
            BuildStatus(state, status, options.Selected),
            BuildGrid(state, options.Selected, floorRows, compact),
            BuildMetrics(state, options.Selected),
            BuildOps(state),
            BuildTail(state, tailLines),
            status.ReadOnly
                ? "space pause · n step · +/- speed · [ ] seek ∓60 s · 0 restart · ←/→ select · q quit"
                : "space pause · n step · +/- speed · ←/→ select · m maint · e emergency · s stuck 60 s · [ ] seek ∓60 s · q quit",
            showSide);
    }

    // ---- Status ---------------------------------------------------------------------------------------

    private static StatusBar BuildStatus(BuildingState s, StatusInfo info, int selected) => new(
        info.Source,
        $"{s.Scenario} · {s.Scheduling}+{s.Dispatch} · seed {s.Seed}",
        Clock(s.Now),
        info.TargetSpeed,
        info.Achieved is { } a ? a.ToString("0.0", Inv) + "×" : "–",
        info.Paused,
        s.Ended,
        info.ReadOnly ? "read-only" : info.InjectorNoOp ? "faults (no-op)" : "faults",
        info.InjectorNoOp,
        $"E{selected}",
        info.Message);

    /// <summary>Sim time as h:mm:ss.f.</summary>
    public static string Clock(SimTime t)
    {
        var ms = t.Ms;
        return string.Create(Inv, $"{ms / 3_600_000}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms / 100 % 10}");
    }

    // ---- Building -------------------------------------------------------------------------------------

    private static BuildingGrid BuildGrid(BuildingState s, int selected, int floorRows, bool compact)
    {
        // A short terminal shows a window of floors, centred on the selected elevator so it never scrolls away.
        var focus = selected < s.Elevators.Count ? s.Elevators[selected].Floor : Floors.Lowest;
        var top = Math.Clamp(focus + floorRows / 2, Math.Min(s.Floors, floorRows), s.Floors);
        var bottom = top - floorRows + 1;

        var rows = new List<FloorRow>(floorRows);
        for (var floor = top; floor >= bottom; floor--)
        {
            var shafts = new ShaftCell[s.Elevators.Count];
            for (var i = 0; i < shafts.Length; i++)
            {
                var e = s.Elevators[i];
                shafts[i] = e.Floor == floor ? Cell(e, s.Capacity, compact, i == selected) : ShaftCell.Empty;
            }
            rows.Add(new FloorRow(floor,
                s.Waiting.GetValueOrDefault((floor, Direction.Up)),
                s.Waiting.GetValueOrDefault((floor, Direction.Down)),
                shafts));
        }
        return new BuildingGrid([.. s.Elevators.Select(e => $"E{e.Id}")], rows, compact, s.Floors - top, bottom - Floors.Lowest);
    }

    /// <summary>
    /// The elevator glyph: ▲ / ▼ moving, ■ idle, ◧ doors opening, □ open, ◨ closing, ✖ parked (maintenance);
    /// then the lantern (↑ / ↓) while the doors are open or closing; the load; and a mode marker.
    /// </summary>
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

    // ---- Metrics and ops ------------------------------------------------------------------------------

    private static MetricsPanel BuildMetrics(BuildingState s, int selected)
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
        var rows = s.Elevators.Select(e => new ElevatorRow(
            $"E{e.Id}",
            JsonNamingPolicy.SnakeCaseUpper.ConvertName(e.State.ToString()),
            Load(e.Riders, s.Capacity),
            string.Create(Inv, $"{Math.Round(100 * e.Utilization(s.Now)):0} %"),
            Marker(e.Mode),
            e.Id == selected)).ToList();

        return new MetricsPanel(avg, p95, max, longWaits,
            string.Create(Inv, $"{s.Delivered} / {s.ExpectedPassengers}"),
            string.Create(Inv, $"{active} ({waiting} waiting, {active - waiting} riding)"),
            rows);
    }

    private static OpsPanel BuildOps(BuildingState s)
    {
        long emergencyMs = 0, faultMs = 0;
        int stops = 0, faults = 0;
        var rows = new List<OpsRow>(s.Elevators.Count);
        foreach (var e in s.Elevators)
        {
            var em = e.ModeMs(ElevatorMode.Emergency, s.Now);
            var fm = e.ModeMs(ElevatorMode.Fault, s.Now);
            emergencyMs += em;
            faultMs += fm;
            stops += e.EmergencyStops;
            faults += e.Faults;
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

    private static string Seconds1(double s) => s.ToString("0.0", Inv) + " s";

    private static string Seconds0(long ms) => (ms / 1000.0).ToString("0", Inv) + " s";

    // ---- Event tail -----------------------------------------------------------------------------------

    private static List<TailLine> BuildTail(BuildingState s, int lines)
    {
        var tail = s.Tail;
        var start = Math.Max(0, tail.Count - lines);
        var result = new List<TailLine>(tail.Count - start);
        for (var i = start; i < tail.Count; i++) result.Add(EventLine.Format(tail[i]));
        return result;
    }
}
