namespace Elevator.Tui.View;

/// <summary>
/// Everything one frame shows, as plain strings and enums: no Spectre types, so it is unit-tested on its own and the
/// renderer is a dumb mapping (ADR-011). Built by <see cref="ViewModel.Build"/> from a BuildingState.
/// </summary>
public sealed record ScreenModel(
    StatusBar Status,
    BuildingGrid Building,
    MetricsPanel Metrics,
    OpsPanel Ops,
    IReadOnlyList<TailLine> Tail,
    string Help,
    bool ShowSidePanel);

/// <summary>Colour intent; the renderer maps it to a style.</summary>
public enum Tone { Normal, Dim, Accent, Good, Warn, Bad }

public enum CellKind { Empty, Idle, Moving, Doors, OutOfService }

/// <summary>Operating-mode marker drawn next to an elevator (D, M, E, F), from the v3 mode events.</summary>
public enum ModeMarker { None, Draining, Maintenance, Emergency, Fault }

public sealed record StatusBar(
    string Source,
    string Title,
    string Clock,
    string TargetSpeed,
    string AchievedSpeed,
    bool Paused,
    bool Finished,
    string Injector,
    bool InjectorNoOp,
    string Selected,
    string? Message);

/// <param name="Rows">Top floor first. May be a window of the building when the terminal is short.</param>
/// <param name="Compact">Cells show the glyph only (narrow terminal).</param>
public sealed record BuildingGrid(IReadOnlyList<string> ShaftHeaders, IReadOnlyList<FloorRow> Rows, bool Compact, int HiddenAbove, int HiddenBelow);

public sealed record FloorRow(int Floor, int WaitingUp, int WaitingDown, IReadOnlyList<ShaftCell> Shafts);

/// <summary>One shaft at one floor: empty, or the elevator's glyph (state, direction, load, mode marker).</summary>
public sealed record ShaftCell(string Text, CellKind Kind, ModeMarker Marker, bool Selected)
{
    public static ShaftCell Empty { get; } = new("", CellKind.Empty, ModeMarker.None, false);
}

public sealed record MetricsPanel(
    string WaitAvg,
    string WaitP95,
    string WaitMax,
    string LongWaits,
    string Served,
    string Active,
    IReadOnlyList<ElevatorRow> Elevators);

public sealed record ElevatorRow(string Name, string State, string Load, string Utilization, ModeMarker Marker, bool Selected);

/// <summary>Operations (Hard stage): zeros until maintenance, emergency and fault events appear in the log.</summary>
public sealed record OpsPanel(
    IReadOnlyList<OpsRow> Elevators,
    string EmergencyStops,
    string Faults,
    string Reassignments,
    string Commands,
    string Denials);

public sealed record OpsRow(string Name, string MaintenanceTime, string Emergency, string Faults);

/// <param name="Costs">For ASSIGN lines: every candidate's cost (∞ = ineligible) and the winner; empty otherwise.</param>
public sealed record TailLine(string Time, string Kind, string Text, Tone Tone, IReadOnlyList<CostCell> Costs);

public sealed record CostCell(string Elevator, string Cost, bool Winner);

/// <summary>What the loop knows that the event log does not: speeds, pause, the injector, the last key's message.</summary>
public sealed record StatusInfo(
    string Source = "LIVE",
    string TargetSpeed = "1×",
    double? Achieved = null,
    bool Paused = false,
    bool InjectorNoOp = true,
    string? Message = null,
    bool ReadOnly = false);

/// <param name="Width">Terminal columns.</param>
/// <param name="Height">Terminal rows.</param>
/// <param name="Selected">Elevator the m / e / s keys act on.</param>
public sealed record ViewOptions(int Width = 120, int Height = 40, int Selected = 0, StatusInfo? Status = null);
