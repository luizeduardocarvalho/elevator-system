namespace Elevator.Domain;

/// <summary>
/// One access rule (ADR-012): may this request use this elevator at all? Floor restrictions, served floors, capacity
/// units, VIP-dedicated elevators. Pure, like every policy: a function of the request and the elevator's configuration
/// (type, served floors, capacity, id), never of its position or load, so the answer for a request never changes and
/// the dispatcher evaluates it once per request.
/// </summary>
public interface IAccessPolicy
{
    /// <summary>Short name, logged in <see cref="Events.AccessDenied"/> (e.g. "served-floors").</summary>
    string Name { get; }

    /// <summary>Null when allowed; otherwise the reason it is not.</summary>
    string? Deny(Request request, ElevatorSnapshot elevator);

    /// <summary>Dispatch priority of a request (lower goes first among pending requests); 0 = normal.</summary>
    int Priority(Request request) => 0;
}

/// <param name="Policy">The policy that refused (the first one in pipeline order).</param>
public sealed record AccessDenial(string Policy, string Reason);

/// <summary>
/// Access policies composed in order (ADR-012). An elevator is allowed for a request when every policy allows it; the
/// first policy that denies gives the logged reason, so the order decides only which reason is reported, never the
/// outcome. Applied by the dispatcher before the dispatch policy: a denied elevator is never a candidate (cost ∞).
/// The empty pipeline allows everything.
/// </summary>
public sealed class AccessPipeline(IReadOnlyList<IAccessPolicy> policies)
{
    public static AccessPipeline AllowAll { get; } = new([]);

    public IReadOnlyList<IAccessPolicy> Policies { get; } = policies;

    /// <summary>The first denial in pipeline order, or null when every policy allows.</summary>
    public AccessDenial? Check(Request request, ElevatorSnapshot elevator)
    {
        foreach (var p in Policies)
            if (p.Deny(request, elevator) is { } reason)
                return new AccessDenial(p.Name, reason);
        return null;
    }

    /// <summary>The most urgent priority any policy gives the request (0 when none does).</summary>
    public int Priority(Request request)
    {
        var priority = 0;
        foreach (var p in Policies) priority = Math.Min(priority, p.Priority(request));
        return priority;
    }
}
