using Elevator.Domain.Control;
using Elevator.Domain.Faults;

namespace Elevator.Runtime;

/// <summary>
/// The actor runtime's fault injector (ADR-013). A command due now goes straight onto its lane (an emergency stop must
/// preempt at once, never wait behind a scheduler). A future command goes through the system's ordered delivery and
/// arrives at its sim time in order of (At, injection order) (ADR-015), as in the simulator's fault scheduler; one
/// Task.Run per future command gave no ordering between commands due at the same or nearby times. Like every other
/// input to the actors, a command is just a message to the single owner of the elevator: no lock, no shared state.
/// </summary>
public sealed class RuntimeFaultInjector(ElevatorSystem system) : IFaultInjector
{
    public bool IsNoOp => false;

    public void Inject(FaultCommand command)
    {
        if (command.ElevatorId < 0 || command.ElevatorId >= system.ElevatorCount)
            throw new ArgumentOutOfRangeException(nameof(command), $"elevator {command.ElevatorId} does not exist (0..{system.ElevatorCount - 1})");
        var to = Address.Elevator(command.ElevatorId);
        if (command.At <= system.Clock.Now) system.Post(to, new ApplyFault(command));
        else system.PostAt(command.At, to, new ApplyFault(command));
    }
}
