using Elevator.Domain;

namespace Elevator.Policy.Dispatch;

/// <summary>
/// Reference lower bound: hands requests to eligible elevators in turn, ignoring where they are.
/// Deterministic: the turn comes from the dispatcher's decision sequence number.
/// </summary>
public sealed class RoundRobinDispatch : IDispatchPolicy
{
    public string Name => "roundrobin";

    public DispatchChoice? Choose(DispatchContext context)
    {
        var eligible = Eligibility.Candidates(context.Candidates).OrderBy(s => s.ElevatorId).ToList();
        if (eligible.Count == 0) return null;
        var chosen = eligible[(int)(context.Sequence % eligible.Count)];
        return new DispatchChoice(chosen.ElevatorId, 0);
    }
}
