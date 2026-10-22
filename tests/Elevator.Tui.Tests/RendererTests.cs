using Elevator.Domain;
using Elevator.Simulation;
using Elevator.Tui.Render;
using Elevator.Tui.State;
using Elevator.Tui.View;

namespace Elevator.Tui.Tests;

public class RendererTests
{
    private static BuildingState MidLunch()
    {
        var engine = SimEngine.Create(Repo.Load("lunch.yaml"), options: new SimEngineOptions(EmitDispatchEvaluations: true));
        engine.AdvanceTo(new SimTime(600_000));
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

    [Fact]
    public void A_full_size_frame_shows_the_building_metrics_and_the_assign_line()
    {
        var model = ViewModel.Build(MidLunch(), new ViewOptions(160, 50, Status: new StatusInfo()));
        var text = SpectreRenderer.RenderToString(model, 160, 50);
        foreach (var expected in new[] { "E0", "E3", "Metrics", "Ops", "Events", "ASSIGN", "faults", "wait avg" })
            Assert.Contains(expected, text, StringComparison.Ordinal);
    }
}
