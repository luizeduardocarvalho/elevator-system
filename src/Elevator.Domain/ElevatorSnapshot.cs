namespace Elevator.Domain;

/// <summary>
/// Immutable, complete view of one elevator at <see cref="At"/>. Policies are pure functions of it, the dispatcher
/// only ever sees elevators through it (possibly stale, see ADR-006), and <see cref="Elevator.FromSnapshot"/> can
/// rebuild an identical elevator from it to project its future route.
/// </summary>
/// <param name="CommittedDirection">Direction of travel while moving; the lit lantern while stopped; null when it has no work.</param>
/// <param name="CarCalls">Pending drop-off floors, ascending.</param>
/// <param name="HallCalls">Pending pickups in registration order (oldest first).</param>
/// <param name="Riders">Requests on board.</param>
/// <param name="PhaseEndsAt">End of the current timed phase (travel segment or door phase), if any.</param>
/// <param name="Version">Increments on every change; lets receivers detect staleness.</param>
public sealed record ElevatorSnapshot(
    int ElevatorId,
    int CurrentFloor,
    ElevatorState State,
    Direction? CommittedDirection,
    IReadOnlyList<int> CarCalls,
    IReadOnlyList<HallCall> HallCalls,
    int Floors,
    IReadOnlyList<Request> Riders,
    int Capacity,
    double BypassThreshold,
    Instant At,
    Instant? PhaseEndsAt,
    Instant ClosingStartedAt,
    long Version)
{
    private DestinationTotals? _riderTotals;

    public IReadOnlyList<Request> Riders
    {
        get;
        init
        {
            field = value;
            _riderTotals = null;
        }
    } = Riders;

    /// <summary>Riders per destination: set by the elevator (maintained incrementally), computed otherwise.</summary>
    public DestinationTotals RiderTotals
    {
        get => _riderTotals ??= DestinationTotals.Of(Riders);
        init => _riderTotals = value;
    }

    public int Load => RiderTotals.TotalLoad;

    /// <summary>
    /// The elevator's own timing (its type's speed and door times, ADR-012). Null in hand-built snapshots: projections
    /// then use the building's default timing.
    /// </summary>
    public ElevatorTiming? Timing { get; init; }

    public ElevatorMode Mode { get; init; } = ElevatorMode.Normal;

    public string TypeName { get; init; } = "standard";

    /// <summary>Floors this elevator serves: bit f = floor f. Default: every floor.</summary>
    public ulong ServedFloors { get; init; } = ulong.MaxValue;

    public bool Serves(int floor) => floor is >= 0 and < 64 && (ServedFloors & (1UL << floor)) != 0;

    /// <summary>Full-load bypass: at or above the threshold it serves its riders only (no pickups).</summary>
    public bool IsFull => Load >= BypassThreshold * Capacity;

    public bool IsMoving => State is ElevatorState.MovingUp or ElevatorState.MovingDown;

    public static ElevatorSnapshot ForPolicy(int floor, ElevatorState state, Direction? committed, IReadOnlyList<int> carCalls,
        IReadOnlyList<HallCall> hallCalls, int floors, int elevatorId = 0) =>
        new(elevatorId, floor, state, committed, carCalls, hallCalls, floors, [], int.MaxValue, 1.0, Instant.Zero, null, Instant.Zero, 0);
}
