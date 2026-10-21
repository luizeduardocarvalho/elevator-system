namespace Elevator.Domain;

/// <summary>
/// A stop to pick people up: floor + direction. In single-elevator (v1) mode this is a lit hall button and
/// <see cref="Requests"/> is empty. In destination-dispatch mode it groups the requests assigned to this elevator at
/// that floor and direction, in arrival order: several requests share one stop (hall-call dedup).
/// </summary>
public sealed record HallCall(int Floor, Direction Direction, SimTime RegisteredAt, IReadOnlyList<Request> Requests)
{
    private DestinationTotals? _totals;

    public HallCall(int floor, Direction direction, SimTime registeredAt) : this(floor, direction, registeredAt, []) { }

    public IReadOnlyList<Request> Requests
    {
        get;
        init
        {
            field = value;
            _totals = null; // any change of requests invalidates the totals unless they are set alongside
        }
    } = Requests;

    /// <summary>Per-destination totals of <see cref="Requests"/>: set by the elevator when it changes them, computed otherwise.</summary>
    public DestinationTotals Totals
    {
        get => _totals ??= DestinationTotals.Of(Requests);
        init => _totals = value;
    }

    public int Load => Totals.TotalLoad;

    // Value equality on the data, not on the cached totals.
    public bool Equals(HallCall? other) =>
        other is not null && Floor == other.Floor && Direction == other.Direction && RegisteredAt == other.RegisteredAt
        && Requests.SequenceEqual(other.Requests);

    public override int GetHashCode() => HashCode.Combine(Floor, Direction, RegisteredAt, Requests.Count);
}
