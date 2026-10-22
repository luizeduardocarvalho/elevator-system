using Elevator.Runtime;
using Elevator.Simulation;

namespace Elevator.Lab;

/// <summary>
/// Maps a lab scenario onto the runtime's options (ADR-019): the runtime knows nothing about scenarios, so the lab does
/// the translation, field by field.
/// </summary>
public static class ScenarioRuntime
{
    public static RuntimeOptions ToRuntimeOptions(this Scenario s, double speed) =>
        new(s.Floors, s.Elevators, s.Capacity, s.BypassThreshold, s.Dispatch, Speed: speed, Timing: s.Timing, Latency: s.Latency)
        {
            Fleet = s.Fleet,
            Access = s.Access,
            WatchdogTimeoutMs = s.WatchdogTimeoutMs,
        };
}
