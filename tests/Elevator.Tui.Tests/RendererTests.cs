using Elevator.Domain;
using Elevator.Simulation;
using Elevator.Tui.Render;
using Elevator.Tui.State;
using Elevator.Tui.View;

namespace Elevator.Tui.Tests;

public class RendererTests
{
    private static BuildingState MidLunch(int elevators = 4)
    {
        var lunch = Repo.Load("lunch.yaml") with { Elevators = elevators };
        var engine = SimEngine.Create(lunch, options: new SimEngineOptions(EmitDispatchEvaluations: true));
        engine.AdvanceTo(new Instant(600_000));
        var state = new BuildingState();
        foreach (var e in engine.Log) state.Apply(e);
        state.AdvanceTo(engine.Now);
        return state;
    }

    [Theory]
    [InlineData(160, 50)]
    [InlineData(120, 40)]
    [InlineData(80, 24)]
    [InlineData(40, 12)]
    [InlineData(20, 6)]
    public void Renders_at_any_size_without_throwing(int width, int height)
    {
        var model = ViewModel.Build(MidLunch(), new ViewOptions(width, height, Status: new StatusInfo("LIVE", "10×", 9.9)));
        var text = SpectreRenderer.RenderToString(model, width, height);
        Assert.Contains("LIVE", text, StringComparison.Ordinal);
        Assert.All(text.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= width, $"line wider than {width}: '{line}'"));
    }

    /// <summary>
    /// Wide fleets used to throw: the side panels listed every elevator and outgrew the rows beside the building, and
    /// the shafts were squeezed past readability. Every fleet size must now render, at every terminal size.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(40)]
    public void Any_fleet_size_renders_at_any_terminal_size(int elevators)
    {
        var state = MidLunch(elevators);
        foreach (var (width, height) in new[] { (200, 60), (160, 50), (120, 40), (120, 20), (80, 24), (40, 12) })
            foreach (var selected in new[] { 0, elevators / 2, elevators - 1 })
            {
                var model = ViewModel.Build(state, new ViewOptions(width, height, selected, new StatusInfo("LIVE", "10×", 9.9)));
                var text = SpectreRenderer.RenderToString(model, width, height);
                Assert.Contains("LIVE", text, StringComparison.Ordinal);
                Assert.All(text.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= width, $"{elevators} elevators at {width}×{height}: line wider than {width}"));
            }
    }

    [Fact]
    public void A_full_size_frame_shows_the_building_metrics_and_the_assign_line()
    {
        var model = ViewModel.Build(MidLunch(), new ViewOptions(160, 50, Status: new StatusInfo()));
        var text = SpectreRenderer.RenderToString(model, 160, 50);
        foreach (var expected in new[] { "E0", "E3", "Metrics", "Ops", "Events", "ASSIGN", "faults", "wait avg" })
            Assert.Contains(expected, text, StringComparison.Ordinal);
    }
}
