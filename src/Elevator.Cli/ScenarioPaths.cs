namespace Elevator.Cli;

internal static class ScenarioPaths
{
    /// <summary>"up-peak" → scenarios/up-peak.yaml; an existing path is used as is.</summary>
    public static string Resolve(string nameOrPath) =>
        File.Exists(nameOrPath) ? nameOrPath : Path.Combine("scenarios", nameOrPath.EndsWith(".yaml", StringComparison.Ordinal) ? nameOrPath : nameOrPath + ".yaml");

    public static string[] List(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
