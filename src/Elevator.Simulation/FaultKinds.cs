using Elevator.Domain.Faults;

namespace Elevator.Simulation;

/// <summary>YAML spelling of <see cref="FaultKind"/>: maintenance-on, maintenance-off, emergency-stop, emergency-clear, stuck, unstick.</summary>
public static class FaultKinds
{
    private static readonly Dictionary<string, FaultKind> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["maintenance-on"] = FaultKind.MaintenanceOn,
        ["maintenance-off"] = FaultKind.MaintenanceOff,
        ["emergency-stop"] = FaultKind.EmergencyStop,
        ["emergency-clear"] = FaultKind.EmergencyClear,
        ["stuck"] = FaultKind.Stuck,
        ["unstick"] = FaultKind.Unstick,
    };

    public static IReadOnlyCollection<string> Names => ByName.Keys;

    public static bool TryParse(string text, out FaultKind kind) => ByName.TryGetValue(text, out kind);

    public static string Name(FaultKind kind) => ByName.First(kv => kv.Value == kind).Key;
}
