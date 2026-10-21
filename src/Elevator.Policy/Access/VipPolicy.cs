using Elevator.Domain;

namespace Elevator.Policy.Access;

/// <summary>
/// VIP service (ADR-012), two independent options:
/// <list type="bullet">
/// <item><b>priority</b>: VIP requests are dispatched first among pending requests (a VIP waiting for an elevator to
/// come back into service gets it before anyone who called earlier);</item>
/// <item><b>dedicated mode</b>: the listed elevators serve VIPs only; everyone else is denied on them. VIPs may still
/// use any other elevator: dispatch picks the best allowed one.</item>
/// </list>
/// </summary>
public sealed class VipPolicy(IReadOnlyCollection<int> dedicated, bool priority) : IAccessPolicy
{
    private readonly HashSet<int> _dedicated = [.. dedicated];

    public string Name => "vip";

    public string? Deny(Request request, ElevatorSnapshot elevator) =>
        !request.Vip && _dedicated.Contains(elevator.ElevatorId) ? $"E{elevator.ElevatorId} is dedicated to VIPs" : null;

    public int Priority(Request request) => priority && request.Vip ? -1 : 0;
}
