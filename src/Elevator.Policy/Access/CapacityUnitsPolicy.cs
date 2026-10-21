using Elevator.Domain;

namespace Elevator.Policy.Access;

/// <summary>
/// A load must fit the elevator's capacity in load units (ADR-012): a pallet that only the freight elevator can carry
/// is never assigned to a local. Without this rule such a request would be rejected for capacity by every elevator and
/// wait forever; with it, it goes to an elevator that can carry it, or is refused at once.
/// </summary>
public sealed class CapacityUnitsPolicy : IAccessPolicy
{
    public string Name => "capacity";

    public string? Deny(Request request, ElevatorSnapshot elevator) =>
        request.Load > elevator.Capacity ? $"load {request.Load} exceeds E{elevator.ElevatorId} ({elevator.TypeName}) capacity {elevator.Capacity}" : null;
}
