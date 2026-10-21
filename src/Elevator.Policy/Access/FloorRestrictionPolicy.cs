using Elevator.Domain;

namespace Elevator.Policy.Access;

/// <summary>
/// Restricted floors (ADR-012): a floor may require a minimum access level, for the pickup as well as the destination
/// (a badge reader in the hall and in the car). A passenger without it is never taken there or from there.
/// </summary>
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
