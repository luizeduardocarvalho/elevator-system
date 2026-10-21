namespace Elevator.Domain;

/// <summary>
/// A passenger request (spec, Medium level): from a pickup floor to a destination floor.
/// The direction is derived, so a valid request can never carry an inconsistent direction.
/// </summary>
public sealed record Request(int PickupFloor, int DestinationFloor, Direction Direction, SimTime Timestamp)
{
    /// <param name="declared">Direction the caller claims (e.g. from an input file); must match pickup→destination.</param>
    public static Result<Request> Create(int pickup, int destination, SimTime timestamp, int floors, Direction? declared = null)
    {
        if (Floors.Check(pickup, floors) is { } pickupError) return Result<Request>.Fail($"pickup: {pickupError}");
        if (Floors.Check(destination, floors) is { } destError) return Result<Request>.Fail($"destination: {destError}");
        if (pickup == destination) return Result<Request>.Fail($"pickup and destination are both floor {pickup}");

        var direction = Directions.Between(pickup, destination)!.Value;
        if (declared is { } d && d != direction)
            return Result<Request>.Fail($"direction {d} is inconsistent with {pickup}→{destination}");

        return Result<Request>.Ok(new Request(pickup, destination, direction, timestamp));
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

/// <summary>Outcome of a call registration through <see cref="ElevatorController"/>; invalid input never throws.</summary>
public sealed record RequestResult(bool Accepted, string? Reason)
{
    public static RequestResult Ok { get; } = new(true, null);

    public static RequestResult Rejected(string reason) => new(false, reason);
}
