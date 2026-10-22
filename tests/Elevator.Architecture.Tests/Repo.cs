namespace Elevator.Architecture.Tests;

internal static class Repo
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ElevatorSystem.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}
