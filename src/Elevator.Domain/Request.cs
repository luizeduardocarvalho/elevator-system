namespace Elevator.Domain;

/// <summary>
/// A passenger request (spec, Medium level): from a pickup floor to a destination floor. In multi-elevator mode this
/// is a destination-dispatch request: the destination is entered at the hall, so the dispatcher and the assigned
/// elevator know it before pickup (ADR-009). The direction is derived, so it can never be inconsistent.
/// </summary>
/// <param name="Timestamp">t_call: when the passenger first pressed. Kept across re-registrations (left behind, released).</param>
/// <param name="Id">Unique per passenger; submissions are idempotent by id.</param>
/// <param name="Load">Load units (a person is 1), so freight can be modelled later.</param>
public sealed record Request(int PickupFloor, int DestinationFloor, Direction Direction, SimTime Timestamp, int Id = 0, int Load = 1)
{
    /// <summary>VIP passenger: allowed on VIP-dedicated elevators (ADR-012); with VIP priority, not charged in the cost function for the delay it adds to others (ADR-015).</summary>
    public bool Vip { get; init; }

    /// <summary>Access level, checked against restricted floors (ADR-012). 0 = no special access.</summary>
    public int AccessLevel { get; init; }

    /// <param name="declared">Direction the caller claims (e.g. from an input file); must match pickup→destination.</param>
    public static Result<Request> Create(int pickup, int destination, SimTime timestamp, int floors, Direction? declared = null, int id = 0, int load = 1)
    {
        var request = new Request(pickup, destination, declared ?? Directions.Between(pickup, destination) ?? Direction.Up, timestamp, id, load);
        return request.Check(floors) is { } error ? Result<Request>.Fail(error) : Result<Request>.Ok(request);
    }

    /// <summary>
    /// Null when this request is valid in a building of <paramref name="floors"/> floors, otherwise the reason. The record
    /// constructor does not validate, so every entry point that accepts a <see cref="Request"/> checks it (ADR-017).
    /// </summary>
    public string? Check(int floors)
    {
        if (Floors.Check(PickupFloor, floors) is { } pickupError) return $"pickup: {pickupError}";
        if (Floors.Check(DestinationFloor, floors) is { } destError) return $"destination: {destError}";
        if (PickupFloor == DestinationFloor) return $"pickup and destination are both floor {PickupFloor}";
        if (Direction != Directions.Between(PickupFloor, DestinationFloor))
            return $"direction {Direction} is inconsistent with {PickupFloor}→{DestinationFloor}";
        if (Load <= 0) return $"load must be > 0 (got {Load})";
        return null;
    }
}

public static class Floors
{
    public const int Lowest = 1;

    /// <summary>Null when <paramref name="floor"/> is in 1..<paramref name="floors"/>, otherwise the reason.</summary>
    public static string? Check(int floor, int floors) =>
        floor < Lowest || floor > floors ? $"floor {floor} is outside {Lowest}..{floors}" : null;

    /// <summary>Null when a hall button for <paramref name="direction"/> exists on <paramref name="floor"/>.</summary>
    public static string? CheckHallButton(int floor, Direction direction, int floors) =>
        Check(floor, floors)
        ?? (floor == Lowest && direction == Direction.Down ? $"there is no DOWN button on floor {Lowest}"
            : floor == floors && direction == Direction.Up ? $"there is no UP button on top floor {floors}"
            : null);
}

public sealed record Result<T>(T? Value, string? Error)
{
    public bool IsSuccess => Error is null;

    public static Result<T> Ok(T value) => new(value, null);

    public static Result<T> Fail(string error) => new(default, error);
}
