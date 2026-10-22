using Elevator.Domain;

namespace Elevator.Policy.Access;

/// <summary>
/// VIP eligibility (ADR-012): the listed elevators serve VIPs only; everyone else is denied on them. VIPs may still use
/// any other elevator: dispatch picks the best allowed one. (VIP priority is not a queue position: it is a cost term,
/// see <see cref="Dispatch.CostBasedDispatch"/> and ADR-015.)
/// </summary>
public sealed class VipPolicy(IReadOnlyCollection<int> dedicated) : IAccessPolicy
{
    private readonly HashSet<int> _dedicated = [.. dedicated];

    public string Name => "vip";

    public string? Deny(Request request, ElevatorSnapshot elevator) =>
        !request.Vip && _dedicated.Contains(elevator.ElevatorId) ? $"E{elevator.ElevatorId} is dedicated to VIPs" : null;
}
