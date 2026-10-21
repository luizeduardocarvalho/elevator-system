namespace Elevator.Simulation.Tests;

internal static class Repo
{
    public static string Root { get; } = Find();

    public static string Scenario(string name) => Path.Combine(Root, "scenarios", name);

    public static Scenario Load(string name)
    {
        var result = ScenarioLoader.LoadFile(Scenario(name));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        return result.Scenario!;
    }

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ElevatorSystem.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    public static string ToJsonl(SimulationResult result)
    {
        using var writer = new StringWriter();
        JsonlEventLog.Write(writer, result.Events);
        return writer.ToString();
    }
}
