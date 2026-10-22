using Elevator.Domain;

namespace Elevator.Policy.Access;

/// <param name="minimumLevel">Floor → minimum <see cref="Request.AccessLevel"/>.</param>
public sealed class FloorRestrictionPolicy(IReadOnlyDictionary<int, int> minimumLevel) : IAccessPolicy
{
    public string Name => "floor-restriction";

    public IReadOnlyDictionary<int, int> MinimumLevel { get; } = minimumLevel;

    public string? Deny(Request request, ElevatorSnapshot elevator) =>
        Check(request.PickupFloor, request) ?? Check(request.DestinationFloor, request);

    private string? Check(int floor, Request r) =>
        MinimumLevel.TryGetValue(floor, out var level) && r.AccessLevel < level ? $"floor {floor} requires access level {level} (has {r.AccessLevel})" : null;
}
