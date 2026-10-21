namespace Elevator.Domain;

/// <summary>
/// A stop to pick people up: floor + direction. In single-elevator (v1) mode this is a lit hall button and
/// <see cref="Requests"/> is empty. In destination-dispatch mode it groups the requests assigned to this elevator at
/// that floor and direction, in arrival order: several requests share one stop (hall-call dedup).
/// </summary>
public sealed record HallCall(int Floor, Direction Direction, SimTime RegisteredAt, IReadOnlyList<Request> Requests)
{
    private DestinationTotals? _totals;
    private int? _maxLoad;

    public HallCall(int floor, Direction direction, SimTime registeredAt) : this(floor, direction, registeredAt, []) { }

    public IReadOnlyList<Request> Requests
    {
        get;
        init
        {
            field = value;
            _totals = null; // any change of requests invalidates the totals unless they are set alongside
            _maxLoad = null;
        }
    } = Requests;

    /// <summary>Per-destination totals of <see cref="Requests"/>: set by the elevator when it changes them, computed otherwise.</summary>
    public DestinationTotals Totals
    {
        get => _totals ??= DestinationTotals.Of(Requests);
        init => _totals = value;
    }

    public int Load => Totals.TotalLoad;

    /// <summary>The heaviest load waiting at this stop (0 for a plain lit button). When it fits, whoever is first in the
    /// queue fits, so a stop here always boards someone (ADR-012).</summary>
    public int MaxLoad => _maxLoad ??= Requests.Count == 0 ? 0 : Requests.Max(r => r.Load);

    // Value equality on the data, not on the cached totals.
    public bool Equals(HallCall? other) =>
        other is not null && Floor == other.Floor && Direction == other.Direction && RegisteredAt == other.RegisteredAt
        && Requests.SequenceEqual(other.Requests);

    public override int GetHashCode() => HashCode.Combine(Floor, Direction, RegisteredAt, Requests.Count);
}
