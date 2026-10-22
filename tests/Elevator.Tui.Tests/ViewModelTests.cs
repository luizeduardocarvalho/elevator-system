using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Tui.State;
using Elevator.Tui.View;

namespace Elevator.Tui.Tests;

public class ViewModelTests
{
    private static Instant S(double seconds) => Instant.FromSeconds(seconds);

    private static BuildingState Fold(params DomainEvent[] events)
    {
        var state = new BuildingState();
        state.Apply(new RunStarted(Instant.Zero, "unit", 3, 6, "look", 10, 3, "cost", 10));
        foreach (var e in events) state.Apply(e);
        return state;
    }

    private static ScreenModel Build(BuildingState state, ViewOptions? options = null) => ViewModel.Build(state, options ?? new ViewOptions());

    private static ShaftCell Cell(ScreenModel m, int floor, int elevator) => m.Building.Rows.Single(r => r.Floor == floor).Shafts[elevator];

    [Fact]
    public void Rows_run_top_floor_first_and_each_elevator_sits_in_its_floor_and_shaft()
    {
        var m = Build(Fold(new StateChanged(S(1), 1, ElevatorState.Idle, ElevatorState.MovingUp, Trigger.StartMoveUp, 1), new FloorReached(S(3), 1, 4)));
        Assert.Equal([6, 5, 4, 3, 2, 1], m.Building.Rows.Select(r => r.Floor));
        Assert.Equal(["E0", "E1", "E2"], m.Building.ShaftHeaders);
        Assert.Equal(CellKind.Moving, Cell(m, 4, 1).Kind);
        Assert.Equal(CellKind.Empty, Cell(m, 1, 1).Kind);
        Assert.Equal((CellKind.Idle, CellKind.Idle), (Cell(m, 1, 0).Kind, Cell(m, 1, 2).Kind));
        Assert.Equal(3, m.Building.Rows.Sum(r => r.Shafts.Count(c => c.Kind != CellKind.Empty)));
    }

    public static TheoryData<ElevatorState, Direction?, string, CellKind> Glyphs => new()
    {
        { ElevatorState.MovingUp, null, "▲ 2/10", CellKind.Moving },
        { ElevatorState.MovingDown, null, "▼ 2/10", CellKind.Moving },
        { ElevatorState.Idle, null, "■ 2/10", CellKind.Idle },
        { ElevatorState.DoorOpening, null, "◧ 2/10", CellKind.Doors },
        { ElevatorState.DoorOpen, Direction.Up, "□↑ 2/10", CellKind.Doors },
        { ElevatorState.DoorClosing, Direction.Down, "◨↓ 2/10", CellKind.Doors },
        { ElevatorState.Maintenance, null, "✖ 2/10", CellKind.OutOfService },
    };

    [Theory]
    [MemberData(nameof(Glyphs))]
    public void Glyph_shows_state_direction_and_load(ElevatorState state, Direction? lantern, string text, CellKind kind)
    {
        var m = Build(Fold(
            new PassengerArrived(S(0), 1, 1, 5), new PassengerArrived(S(0), 2, 1, 6),
            new PassengerBoarded(S(1), 1, 0, 1), new PassengerBoarded(S(1), 2, 0, 1),
            new StateChanged(S(2), 0, ElevatorState.Idle, state, Trigger.StartMoveUp, 1),
            new DoorsOpened(S(2), 0, 1, lantern)));
        Assert.Equal((text, kind), (Cell(m, 1, 0).Text, Cell(m, 1, 0).Kind));
    }

    [Fact]
    public void Unbounded_capacity_shows_the_rider_count_only()
    {
        var state = new BuildingState();
        state.Apply(new RunStarted(Instant.Zero, "v1", 1, 10, "look", 3));
        Assert.Equal("■ 0", Build(state).Building.Rows.Single(r => r.Floor == 1).Shafts[0].Text);
    }

    [Fact]
    public void Mode_markers_come_from_the_v3_events()
    {
        var m = Build(Fold(
            new MaintenanceRequested(S(1), 0), new EmergencyStopped(S(1), 1, 1), new FaultDetected(S(1), 2, 1, 30_000)));
        Assert.Equal([ModeMarker.Draining, ModeMarker.Emergency, ModeMarker.Fault], m.Building.Rows.Single(r => r.Floor == 1).Shafts.Select(c => c.Marker));
        Assert.Equal("■ 0/10 D", Cell(m, 1, 0).Text);
        Assert.Equal("■ 0/10 E", Cell(m, 1, 1).Text);

        var parked = Build(Fold(new MaintenanceRequested(S(1), 0), new MaintenanceEntered(S(2), 0, 1),
            new StateChanged(S(2), 0, ElevatorState.Idle, ElevatorState.Maintenance, Trigger.EnterMaintenance, 1)));
        Assert.Equal(("✖ 0/10 M", ModeMarker.Maintenance), (Cell(parked, 1, 0).Text, Cell(parked, 1, 0).Marker));
        Assert.Equal(ModeMarker.Maintenance, parked.Metrics.Elevators[0].Marker);
    }

    [Fact]
    public void Waiting_counts_are_shown_per_floor_with_up_and_down()
    {
        var m = Build(Fold(new PassengerArrived(S(1), 1, 3, 6), new PassengerArrived(S(1), 2, 3, 5), new PassengerArrived(S(1), 3, 3, 1)));
        var row = m.Building.Rows.Single(r => r.Floor == 3);
        Assert.Equal((2, 1), (row.WaitingUp, row.WaitingDown));
        Assert.Equal((0, 0), (m.Building.Rows.Single(r => r.Floor == 4).WaitingUp, m.Building.Rows.Single(r => r.Floor == 4).WaitingDown));
    }

    [Fact]
    public void Assign_line_lists_every_cost_with_infinity_and_marks_the_winner()
    {
        var m = Build(Fold(new DispatchEvaluated(S(12.5), 42, [new CandidateCost(0, 31.25), new CandidateCost(1, null, "full"), new CandidateCost(2, 8)], 2)));
        var line = m.Tail[^1];
        Assert.Equal(("12.5", "ASSIGN", "R42 → E2"), (line.Time, line.Kind, line.Text));
        Assert.Equal([new CostCell("E0", "31.3", false), new CostCell("E1", "∞", false), new CostCell("E2", "8.0", true)], line.Costs);

        var none = Build(Fold(new DispatchEvaluated(S(1), 7, [new CandidateCost(0, null)], null))).Tail[^1];
        Assert.Equal("R7 → none", none.Text);
        Assert.DoesNotContain(none.Costs, c => c.Winner);
    }

    [Fact]
    public void Unknown_events_render_as_a_generic_key_value_line()
    {
        var state = Fold();
        state.Apply(new UnknownEvent(S(3), "ElevatorTeleported", [new("elevatorId", "2"), new("to", "ROOF")]));
        var line = Build(state).Tail[^1];
        Assert.Equal(("3.0", "ElevatorTeleported", "elevatorId=2 to=ROOF", Tone.Dim), (line.Time, line.Kind, line.Text, line.Tone));
    }

    [Fact]
    public void Every_event_type_formats_without_throwing()
    {
        var t = S(1);
        DomainEvent[] all =
        [
            new RunEnded(t, 1, 0), new PassengerArrived(t, 1, 1, 2), new PassengerBoarded(t, 1, 0, 1), new PassengerAlighted(t, 1, 0, 2),
            new HallCallRegistered(t, 2, Direction.Up), new HallCallAnswered(t, 0, 2, Direction.Down), new CarCallRegistered(t, 0, 4),
            new RequestRejected(t, "HallCall", 9, Direction.Up, "no"), new DoorsOpened(t, 0, 2, null), new RequestSubmitted(t, 1, 1, 2),
            new AssignmentProposed(t, 1, 0, 2, null, true), new AssignmentAccepted(t, 1, 0), new AssignmentRejected(t, 1, 0, AssignmentReason.Stale),
            new AssignmentReleased(t, 1, 0, AssignmentReason.Emergency), new DispatchDeferred(t, 1), new DuplicateSubmission(t, 1),
            new PassengerLeftBehind(t, 1, 0, 1), new OverloadModeChanged(t, true, 300), new MaintenanceRequested(t, 0), new MaintenanceEntered(t, 0, 1),
            new MaintenanceExited(t, 0), new EmergencyStopped(t, 0, 1), new EmergencyCleared(t, 0), new FaultDetected(t, 0, 1, 1), new FaultRecovered(t, 0),
            new AccessDenied(t, 1, null, "vip", "nobody"), new AccessDenied(t, 1, 2, "floors", "13"), new FaultCommandApplied(t, 0, FaultKind.Stuck, 60_000),
        ];
        var state = Fold(all);
        var lines = Build(state, new ViewOptions(Height: 200)).Tail;
        Assert.Equal(all.Length + 1, lines.Count);
        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Kind + l.Text)));
        Assert.Contains(lines, l => l is { Kind: "COMMAND", Text: "E0 STUCK 60 s" });
    }

    [Fact]
    public void Metrics_panel_computes_wait_avg_p95_max_and_long_waits()
    {
        var events = new List<DomainEvent>();
        double[] waits = [10, 20, 30, 70];
        for (var i = 0; i < waits.Length; i++)
        {
            events.Add(new PassengerArrived(S(0), i + 1, 1, 5));
            events.Add(new PassengerBoarded(S(waits[i]), i + 1, 0, 1));
        }
        events.Add(new PassengerArrived(S(71), 9, 2, 5));
        events.Add(new PassengerAlighted(S(80), 1, 0, 5));
        var m = Build(Fold([.. events]));
        Assert.Equal(("32.5", "70.0", "70.0", "25.0 % (1)"), (m.Metrics.WaitAvg, m.Metrics.WaitP95, m.Metrics.WaitMax, m.Metrics.LongWaits));
        Assert.Equal(("1 / 10", "4 (1 waiting, 3 riding)"), (m.Metrics.Served, m.Metrics.Active));
    }

    [Fact]
    public void Empty_metrics_show_dashes()
    {
        var m = Build(Fold());
        Assert.Equal(("–", "–", "–", "–"), (m.Metrics.WaitAvg, m.Metrics.WaitP95, m.Metrics.WaitMax, m.Metrics.LongWaits));
    }

    [Fact]
    public void Per_elevator_rows_show_state_load_and_utilization()
    {
        var state = Fold(new StateChanged(S(0), 1, ElevatorState.Idle, ElevatorState.MovingUp, Trigger.StartMoveUp, 1));
        state.AdvanceTo(S(40));
        var row = Build(state, new ViewOptions(Selected: 1)).Metrics.Elevators[1];
        Assert.Equal(new ElevatorRow("E1", "MOVING_UP", "0/10", "100 %", ModeMarker.None, true), row);
    }

    [Fact]
    public void Ops_panel_shows_zeros_without_v3_events_and_counts_them_when_present()
    {
        var zeros = Build(Fold()).Ops;
        Assert.Equal(("0 (0 s)", "0 (0 s)", "0 (ops 0, +0 s)", "0", "0"), (zeros.EmergencyStops, zeros.Faults, zeros.Reassignments, zeros.Commands, zeros.Denials));
        Assert.All(zeros.Elevators, r => Assert.Equal("0 s", r.MaintenanceTime));

        var busy = Fold(new MaintenanceEntered(S(10), 2, 1), new EmergencyStopped(S(10), 0, 1), new EmergencyCleared(S(40), 0),
            new FaultDetected(S(50), 1, 1, 30_000));
        busy.AdvanceTo(S(70));
        var ops = Build(busy).Ops;
        Assert.Equal(("1 (30 s)", "1 (20 s)", "60 s"), (ops.EmergencyStops, ops.Faults, ops.Elevators[2].MaintenanceTime));
    }

    [Fact]
    public void Status_bar_shows_faults_or_read_only_speed_and_pause()
    {
        var status = new StatusInfo("LIVE", "10×", 9.84, Paused: true, Message: "m: maintenance-on E1");
        var bar = Build(Fold(), new ViewOptions(Status: status, Selected: 1)).Status;
        Assert.Equal(("LIVE", "unit · look+cost · seed 3", "10×", "9.8×", true), (bar.Source, bar.Title, bar.TargetSpeed, bar.AchievedSpeed, bar.Paused));
        Assert.Equal(("faults", "E1", "m: maintenance-on E1"), (bar.Injector, bar.Selected, bar.Message));
        Assert.Equal("read-only", Build(Fold(), new ViewOptions(Status: status with { ReadOnly = true })).Status.Injector);
    }

    [Fact]
    public void Narrow_terminals_get_compact_cells_and_no_side_panel()
    {
        var wide = Build(Fold(), new ViewOptions(Width: 160));
        Assert.Equal((false, true), (wide.Building.Compact, wide.ShowSidePanel));
        var narrow = Build(Fold(), new ViewOptions(Width: 40));
        Assert.Equal((true, false), (narrow.Building.Compact, narrow.ShowSidePanel));
        Assert.Equal("■", narrow.Building.Rows.Single(r => r.Floor == 1).Shafts[0].Text);
    }

    [Fact]
    public void A_tiny_terminal_is_flagged_too_small()
    {
        Assert.True(Build(Fold(), new ViewOptions(Width: 30, Height: 40)).TooSmall);
        Assert.True(Build(Fold(), new ViewOptions(Width: 120, Height: 8)).TooSmall);
        Assert.False(Build(Fold(), new ViewOptions(Width: 40, Height: 12)).TooSmall);
    }

    [Fact]
    public void Short_terminals_show_a_window_of_floors_around_the_selected_elevator()
    {
        var state = Fold(new FloorReached(S(1), 2, 5));
        var m = Build(state, new ViewOptions(Height: 16, Selected: 2));
        Assert.InRange(m.Building.Rows.Count, 1, 5);
        Assert.Contains(m.Building.Rows, r => r.Floor == 5);
        Assert.Equal(6 - m.Building.Rows.Count, m.Building.HiddenAbove + m.Building.HiddenBelow);
        Assert.InRange(m.Tail.Count, 0, 3);
    }

    [Fact]
    public void Building_and_tail_are_deterministic_functions_of_the_state()
    {
        var state = Fold(new PassengerArrived(S(1), 1, 3, 6));
        Assert.Equal(Json(Build(state)), Json(Build(state)));
    }

    private static BuildingState Fleet(int elevators)
    {
        var state = new BuildingState();
        state.Apply(new RunStarted(Instant.Zero, "wide", 1, 20, "look", 0, elevators, "cost", 10));
        return state;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(19)]
    public void A_fleet_too_wide_for_the_terminal_shows_a_window_of_shafts_around_the_selected_elevator(int selected)
    {
        var grid = Build(Fleet(20), new ViewOptions(120, 40, selected)).Building;

        var shown = grid.ShaftHeaders.Select(h => int.Parse(h[1..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.InRange(shown.Count, 1, 19);
        Assert.Contains(selected, shown);
        Assert.Equal(Enumerable.Range(shown[0], shown.Count), shown);
        Assert.Equal((shown[0], 20 - 1 - shown[^1]), (grid.HiddenLeft, grid.HiddenRight));
        Assert.All(grid.Rows, r => Assert.Equal(shown.Count, r.Shafts.Count));
        Assert.True(grid.Compact);
    }

    [Fact]
    public void Side_panels_list_only_the_elevators_that_fit_around_the_selected_one()
    {
        var m = Build(Fleet(20), new ViewOptions(120, 40, 12));

        Assert.InRange(m.Metrics.Elevators.Count, 1, 19);
        Assert.Equal(m.Metrics.Elevators.Select(e => e.Name), m.Ops.Elevators.Select(e => e.Name));
        Assert.Contains(m.Metrics.Elevators, e => e is { Name: "E12", Selected: true });
        Assert.Matches(@"^E\d+(–E\d+)? of 20$", m.Metrics.Range);
        Assert.Equal(m.Metrics.Range, m.Ops.Range);
    }

    [Fact]
    public void A_fleet_that_fits_is_shown_whole_with_no_range()
    {
        var m = Build(Fold(), new ViewOptions(120, 40));

        Assert.Equal(["E0", "E1", "E2"], m.Building.ShaftHeaders);
        Assert.Equal((0, 0), (m.Building.HiddenLeft, m.Building.HiddenRight));
        Assert.Equal(3, m.Metrics.Elevators.Count);
        Assert.Null(m.Metrics.Range);
    }

    internal static string Json(ScreenModel m) => System.Text.Json.JsonSerializer.Serialize(m);
}
