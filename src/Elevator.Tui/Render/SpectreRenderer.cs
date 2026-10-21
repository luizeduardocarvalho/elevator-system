using Elevator.Tui.View;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Elevator.Tui.Render;

/// <summary>
/// Turns a <see cref="ScreenModel"/> into Spectre renderables: a dumb mapping from tones and kinds to styles, with
/// no logic worth testing beyond "it renders at any size". It never sees the engine or the BuildingState (ADR-011).
/// </summary>
public static class SpectreRenderer
{
    public static IRenderable Render(ScreenModel m)
    {
        if (m.TooSmall)
            return new Rows(Status(m.Status), new Markup($"[yellow]terminal too small: need {ViewModel.MinWidth}×{ViewModel.MinHeight}[/]") { Overflow = Overflow.Ellipsis });

        var buildingRows = m.Building.Rows.Count + 4; // rounded table: top border, header, separator, bottom border
        var side = new Layout("side").Size(ViewModel.SidePanelWidth).SplitRows(
            new Layout("metrics").Update(Metrics(m.Metrics)),
            new Layout("ops").Size(m.Ops.Elevators.Count + 7).Update(Ops(m.Ops)));
        if (!m.ShowSidePanel) side.Invisible();

        return new Layout("root").SplitRows(
            new Layout("status").Size(1).Update(Status(m.Status)),
            new Layout("main").Size(buildingRows).SplitColumns(new Layout("building").Update(Building(m.Building)), side),
            new Layout("tail").Update(Tail(m.Tail)),
            new Layout("help").Size(1).Update(new Markup($"[grey]{Markup.Escape(m.Help)}[/]") { Overflow = Overflow.Ellipsis }));
    }

    /// <summary>Renders one frame as plain text at a fixed size (CI smoke runs, <c>--plain</c>, tests).</summary>
    public static string RenderToString(ScreenModel m, int width, int height)
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;
        console.Profile.Height = height;
        console.Write(Render(m));
        return writer.ToString();
    }

    private static Markup Status(StatusBar s)
    {
        var parts = new List<string>
        {
            $"[black on {(s.Source == "LIVE" ? "green" : "aqua")}] {Markup.Escape(s.Source)} [/]",
            Markup.Escape(s.Title),
            $"[bold]{Markup.Escape(s.Clock)}[/]",
            $"speed {Markup.Escape(s.TargetSpeed)} (achieved {Markup.Escape(s.AchievedSpeed)})",
        };
        if (s.Paused) parts.Add("[yellow]⏸ PAUSED[/]");
        if (s.Finished) parts.Add("[aqua]FINISHED[/]");
        parts.Add($"sel [bold]{Markup.Escape(s.Selected)}[/]");
        parts.Add(s.InjectorNoOp ? $"[grey]{Markup.Escape(s.Injector)}[/]" : Markup.Escape(s.Injector));
        if (s.Message is { } msg) parts.Add($"[italic]{Markup.Escape(msg)}[/]");
        return new Markup(string.Join(" · ", parts)) { Overflow = Overflow.Ellipsis };
    }

    private static Table Building(BuildingGrid g)
    {
        var floorHeader = g.HiddenAbove + g.HiddenBelow == 0 ? "Fl" : $"Fl ⇡{g.HiddenAbove}⇣{g.HiddenBelow}";
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn(new TableColumn(floorHeader).RightAligned().NoWrap());
        table.AddColumn(new TableColumn("Waiting").NoWrap());
        foreach (var h in g.ShaftHeaders) table.AddColumn(new TableColumn(Markup.Escape(h)).Centered().NoWrap());

        foreach (var row in g.Rows)
        {
            var cells = new List<IRenderable>(2 + row.Shafts.Count)
            {
                new Markup($"[grey]{row.Floor}[/]"),
                new Markup(Waiting(row)),
            };
            foreach (var c in row.Shafts) cells.Add(new Markup(Cell(c)) { Overflow = Overflow.Crop });
            table.AddRow(cells);
        }
        return table;
    }

    private static string Waiting(FloorRow r)
    {
        var up = r.WaitingUp > 0 ? $"[yellow]▲{r.WaitingUp}[/]" : "[grey]  [/]";
        var down = r.WaitingDown > 0 ? $"[yellow]▼{r.WaitingDown}[/]" : "";
        return $"{up} {down}";
    }

    private static string Cell(ShaftCell c)
    {
        if (c.Kind == CellKind.Empty) return "[grey23]·[/]";
        var color = c.Marker switch
        {
            ModeMarker.Draining => "yellow",
            ModeMarker.Maintenance => "blue",
            ModeMarker.Emergency => "red",
            ModeMarker.Fault => "magenta",
            _ => c.Kind switch
            {
                CellKind.Moving => "green",
                CellKind.Doors => "aqua",
                CellKind.OutOfService => "blue",
                _ => "silver",
            },
        };
        return $"[{color}{(c.Selected ? " bold underline" : "")}]{Markup.Escape(c.Text)}[/]";
    }

    private static Panel Metrics(MetricsPanel m)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]wait avg / p95 / max[/]", Markup.Escape($"{m.WaitAvg} / {m.WaitP95} / {m.WaitMax}"));
        grid.AddRow("[grey]long waits (> 60 s)[/]", Markup.Escape(m.LongWaits));
        grid.AddRow("[grey]served[/]", Markup.Escape(m.Served));
        grid.AddRow("[grey]active[/]", Markup.Escape(m.Active));

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey)
            .AddColumn("").AddColumn("state").AddColumn(new TableColumn("load").RightAligned()).AddColumn(new TableColumn("util").RightAligned());
        foreach (var e in m.Elevators)
        {
            var name = Markup.Escape(e.Name + (e.Marker == ModeMarker.None ? "" : " " + ViewModel.MarkerLetter(e.Marker)));
            table.AddRow(e.Selected ? $"[bold underline]{name}[/]" : name, Markup.Escape(e.State), Markup.Escape(e.Load), Markup.Escape(e.Utilization));
        }
        return new Panel(new Rows(grid, table)).Header("Metrics [grey](sim time)[/]").BorderColor(Color.Grey).Expand();
    }

    private static Panel Ops(OpsPanel o)
    {
        var grid = new Grid().AddColumn().AddColumn().AddColumn().AddColumn();
        grid.AddRow("[grey]elev[/]", "[grey]maint[/]", "[grey]emerg (time)[/]", "[grey]faults (time)[/]");
        foreach (var r in o.Elevators)
            grid.AddRow(Markup.Escape(r.Name), Markup.Escape(r.MaintenanceTime), Markup.Escape(r.Emergency), Markup.Escape(r.Faults));
        var totals = new Grid().AddColumn().AddColumn();
        totals.AddRow("[grey]reassignments[/]", Markup.Escape(o.Reassignments));
        totals.AddRow("[grey]commands / denials[/]", Markup.Escape($"{o.Commands} / {o.Denials}"));
        return new Panel(new Rows(grid, totals)).Header("Ops").BorderColor(Color.Grey).Expand();
    }

    private static Panel Tail(IReadOnlyList<TailLine> lines)
    {
        var rows = new List<IRenderable>(lines.Count);
        foreach (var l in lines)
        {
            var color = l.Tone switch
            {
                Tone.Dim => "grey",
                Tone.Accent => "aqua",
                Tone.Good => "green",
                Tone.Warn => "yellow",
                Tone.Bad => "red",
                _ => "default",
            };
            var costs = l.Costs.Count == 0 ? "" : "  " + string.Join(" · ", l.Costs.Select(c =>
                c.Winner ? $"[bold green]{Markup.Escape(c.Elevator)} {Markup.Escape(c.Cost)}[/]" : $"{Markup.Escape(c.Elevator)} {Markup.Escape(c.Cost)}"));
            rows.Add(new Markup($"[grey]{Markup.Escape(l.Time),8}[/] [{color}]{Markup.Escape(l.Kind),-9}[/] {Markup.Escape(l.Text)}{costs}")
                { Overflow = Overflow.Ellipsis });
        }
        return new Panel(new Rows(rows)).Header("Events").BorderColor(Color.Grey).Expand();
    }
}
