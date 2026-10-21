using Elevator.Simulation;

namespace Elevator.Tui.Tests;

internal static class Repo
{
    public static string Root { get; } = Find();

    public static Scenario Load(string name)
    {
        var result = ScenarioLoader.LoadFile(Path.Combine(Root, "scenarios", name));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        return result.Scenario!;
    }

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ElevatorSystem.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    public static string ToJsonl(IEnumerable<Elevator.Domain.Events.DomainEvent> events)
    {
        using var writer = new StringWriter();
        writer.NewLine = "\n";
        JsonlEventLog.Write(writer, events);
        return writer.ToString();
    }
}
