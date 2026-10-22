using Elevator.Domain;

namespace Elevator.Policy.Access;

public sealed class CapacityUnitsPolicy : IAccessPolicy
{
    public string Name => "capacity";

    public string? Deny(Request request, ElevatorSnapshot elevator) =>
        request.Load > elevator.Capacity ? $"load {request.Load} exceeds E{elevator.ElevatorId} ({elevator.TypeName}) capacity {elevator.Capacity}" : null;
}
