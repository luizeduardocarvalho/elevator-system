using Elevator.Domain;

namespace Elevator.Policy.Access;

/// <summary>An elevator can only take a request when its type serves both the pickup and the destination (ADR-012).</summary>
public sealed class ServedFloorsPolicy : IAccessPolicy
{
    public string Name => "served-floors";

    public string? Deny(Request request, ElevatorSnapshot elevator) =>
        !FloorMask.Contains(elevator.ServedFloors, request.PickupFloor) ? NotServed(elevator, request.PickupFloor)
        : !FloorMask.Contains(elevator.ServedFloors, request.DestinationFloor) ? NotServed(elevator, request.DestinationFloor)
        : null;

    private static string NotServed(ElevatorSnapshot e, int floor) => $"E{e.ElevatorId} ({e.TypeName}) does not serve floor {floor}";
}
