using Elevator.Domain;
using Elevator.Domain.Faults;

namespace Elevator.Simulation;

/// <param name="Elevators">Number of elevators; all start idle at the lowest floor.</param>
/// <param name="Capacity">Per elevator, in load units (a person is 1).</param>
/// <param name="BypassThreshold">Fraction of capacity at which an elevator stops taking pickups (full-load bypass).</param>
/// <param name="Profile">Time-varying random traffic (up-peak, down-peak, lunch...).</param>
public sealed record Scenario(
    string Name,
    int Seed,
    int Floors,
    ElevatorTiming Timing,
    string Scheduling,
    SimTime MaxSimTime,
    IReadOnlyList<PassengerSpec> Passengers,
    PoissonTraffic? Traffic,
    int Elevators = 1,
    int Capacity = 10,
    double BypassThreshold = 0.8,
    string Dispatch = "cost",
    TrafficProfile? Profile = null,
    MessageLatency? Latency = null,
    CapacityMode CapacityMode = CapacityMode.NextTrip,
    IReadOnlyList<FaultCommand>? Faults = null)
{
    /// <summary>Scheduled fault and operator commands (shared v3 contract), applied at their sim time (ADR-013).</summary>
    public IReadOnlyList<FaultCommand> FaultSchedule => Faults ?? [];

    /// <summary>
    /// The type of each elevator, by index (ADR-012). Null = <see cref="Elevators"/> standard elevators built from
    /// <see cref="Timing"/> and <see cref="Capacity"/> (the default; behaves exactly as before types existed).
    /// </summary>
    public IReadOnlyList<ElevatorType>? Fleet { get; init; }

    public IReadOnlyList<ElevatorType> EffectiveFleet =>
        Fleet ?? [.. Enumerable.Repeat(ElevatorType.Standard(Timing, Capacity), Elevators)];

    /// <summary>Floor restrictions and VIP service (ADR-012). Default: none.</summary>
    public AccessConfig Access { get; init; } = AccessConfig.None;

    /// <summary>Default watchdog timeout T (ADR-013): 30 s of sim time without progress while having work.</summary>
    public const long DefaultWatchdogTimeoutMs = 30_000;

    /// <summary>Watchdog timeout in sim ms (YAML <c>watchdog.timeoutS</c>); null = no watchdog.</summary>
    public long? WatchdogTimeoutMs { get; init; } = DefaultWatchdogTimeoutMs;

    /// <summary>
    /// Time of day at sim time 0 (YAML <c>clockStart: "07:00"</c>), for display only (the heatmap's hour labels). It never
    /// reaches the engine or the event log; null = label elapsed time.
    /// </summary>
    public TimeSpan? ClockStart { get; init; }
}

/// <summary>Access configuration of a building (ADR-012); YAML <c>access:</c>.</summary>
/// <param name="RestrictedFloors">Floor → minimum access level to be picked up there or taken there.</param>
/// <param name="VipElevators">Elevators dedicated to VIPs (everyone else is denied on them).</param>
/// <param name="VipPriority">A VIP request does not pay for the delay it adds to others in the cost function, so it takes
/// the fastest pickup (ADR-015). It never reorders requests.</param>
public sealed record AccessConfig(IReadOnlyDictionary<int, int> RestrictedFloors, IReadOnlyList<int> VipElevators, bool VipPriority)
{
    public static AccessConfig None { get; } = new(new Dictionary<int, int>(), [], false);
}

/// <summary>An explicitly scripted passenger: arrives at <paramref name="At"/> on <paramref name="From"/>, wants <paramref name="To"/>.</summary>
public sealed record PassengerSpec(SimTime At, int From, int To)
{
    /// <summary>VIP passenger (priority, may use VIP-dedicated elevators; ADR-012).</summary>
    public bool Vip { get; init; }

    /// <summary>Access level for restricted floors (0 = none).</summary>
    public int AccessLevel { get; init; }

    /// <summary>Load units (a person is 1; freight more).</summary>
    public int Load { get; init; } = 1;
}

public sealed record Passenger(int Id, SimTime ArrivesAt, int From, int To)
{
    public Direction Direction => Directions.Between(From, To)!.Value;

    public bool Vip { get; init; }

    public int AccessLevel { get; init; }

    public int Load { get; init; } = 1;

    public Request ToRequest() => new(From, To, Direction, ArrivesAt, Id, Load) { Vip = Vip, AccessLevel = AccessLevel };
}

/// <summary>Sim-time delays on control messages, to model a dispatcher working from stale snapshots (default: none).</summary>
public sealed record MessageLatency(long SnapshotMs = 0, long DispatchMs = 0)
{
    public static MessageLatency None { get; } = new();
}
