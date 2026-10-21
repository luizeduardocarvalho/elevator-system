using Elevator.Domain;
using Elevator.Simulation;

namespace Elevator.Runtime;

/// <param name="Speed">Sim seconds per wall second (1 = real time).</param>
/// <param name="RequestQueueCapacity">Bound of the request channel; submitters wait when it is full (backpressure).</param>
/// <param name="KeepEvents">Keep the event log in memory (tests, cross-checks). Off for soaks.</param>
public sealed record RuntimeOptions(
    int Floors = 20,
    int Elevators = 4,
    int Capacity = 10,
    double BypassThreshold = 0.8,
    string Scheduling = "look",
    string Dispatch = "cost",
    bool Rebalance = false,
    double Speed = 1.0,
    int RequestQueueCapacity = 1_024,
    bool KeepEvents = true,
    ElevatorTiming? Timing = null,
    MessageLatency? Latency = null,
    int? OverloadThreshold = SimEngine.OverloadThreshold,
    CapacityMode CapacityMode = CapacityMode.NextTrip)
{
    public ElevatorTiming EffectiveTiming => Timing ?? ElevatorTiming.Default;

    /// <summary>Each elevator's type (ADR-012); null = <see cref="Elevators"/> standard elevators.</summary>
    public IReadOnlyList<ElevatorType>? Fleet { get; init; }

    public IReadOnlyList<ElevatorType> EffectiveFleet =>
        Fleet ?? [.. Enumerable.Repeat(ElevatorType.Standard(EffectiveTiming, Capacity), Elevators)];

    /// <summary>
    /// Create the system without starting its actors; <see cref="ElevatorSystem.Resume"/> starts them. Lets a caller
    /// queue messages first (tests of mailbox priority).
    /// </summary>
    public bool StartPaused { get; init; }

    /// <summary>Floor restrictions and VIP service (ADR-012).</summary>
    public AccessConfig Access { get; init; } = AccessConfig.None;

    /// <summary>Watchdog timeout in sim ms (ADR-013); null = no watchdog.</summary>
    public long? WatchdogTimeoutMs { get; init; } = Scenario.DefaultWatchdogTimeoutMs;

    public static RuntimeOptions From(Scenario s, double speed) =>
        new(s.Floors, s.Elevators, s.Capacity, s.BypassThreshold, s.Scheduling, s.Dispatch, Speed: speed, Timing: s.Timing, Latency: s.Latency,
            CapacityMode: s.CapacityMode)
        {
            Fleet = s.Fleet,
            Access = s.Access,
            WatchdogTimeoutMs = s.WatchdogTimeoutMs,
        };
}
