using Elevator.Domain.Control;
using Elevator.Domain.Faults;

namespace Elevator.Runtime;

/// <summary>
/// The actor runtime's fault injector (ADR-013), built on <see cref="ElevatorSystem.Post"/>. A command due now is posted
/// at once; a future one waits on the system's clock (sim time at the system's speed) and is posted then. Like every
/// other input to the actors, it is just a message to the single owner of the elevator: no lock, no shared state.
/// </summary>
public sealed class RuntimeFaultInjector(ElevatorSystem system) : IFaultInjector
{
    public bool IsNoOp => false;

    public void Inject(FaultCommand command)
    {
        if (command.ElevatorId < 0 || command.ElevatorId >= system.ElevatorCount)
            throw new ArgumentOutOfRangeException(nameof(command), $"elevator {command.ElevatorId} does not exist (0..{system.ElevatorCount - 1})");

        var wait = system.Clock.WallUntil(command.At);
        if (wait <= TimeSpan.Zero)
        {
            system.Post(Address.Elevator(command.ElevatorId), new ApplyFault(command));
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(wait, system.Stopping).ConfigureAwait(false);
                system.Post(Address.Elevator(command.ElevatorId), new ApplyFault(command));
            }
            catch (OperationCanceledException)
            {
                // the system stopped first
            }
        });
    }
}
