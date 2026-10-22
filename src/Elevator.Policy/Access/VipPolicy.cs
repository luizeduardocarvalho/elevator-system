using Elevator.Domain;

namespace Elevator.Policy.Access;

public sealed class VipPolicy(IReadOnlyCollection<int> dedicated) : IAccessPolicy
{
    private readonly HashSet<int> _dedicated = [.. dedicated];

    public string Name => "vip";

    public string? Deny(Request request, ElevatorSnapshot elevator) =>
        !request.Vip && _dedicated.Contains(elevator.ElevatorId) ? $"E{elevator.ElevatorId} is dedicated to VIPs" : null;
}
