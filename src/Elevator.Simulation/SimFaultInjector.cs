using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Faults;

namespace Elevator.Simulation;

/// <summary>
/// The simulator's fault injector (ADR-013), built on <see cref="SimEngine.Post"/>: a command goes to the engine's
/// fault scheduler at the current sim time, which delivers it to its elevator at max(command.At, now). Everything
/// enters in sim time, so a run with injected commands is as deterministic and replayable as any other.
/// </summary>
public sealed class SimFaultInjector(SimEngine engine) : IFaultInjector
{
    /// <summary>For <see cref="SimEngineOptions.FaultInjector"/>.</summary>
    public static Func<SimEngine, IFaultInjector> Factory { get; } = e => new SimFaultInjector(e);

    public bool IsNoOp => false;

    public void Inject(FaultCommand command)
    {
        if (command.ElevatorId < 0 || command.ElevatorId >= engine.Scenario.Elevators)
            throw new ArgumentOutOfRangeException(nameof(command), $"elevator {command.ElevatorId} does not exist (0..{engine.Scenario.Elevators - 1})");
        engine.Post(FaultScheduler.Address, new ScheduleFault(command));
    }
}

/// <summary>
/// Holds injected commands until their sim time (an engine internal, ADR-013). Handling <see cref="ScheduleFault"/> at
/// <c>now</c> sends <see cref="ApplyFault"/> to the elevator with a delay of max(0, At − now): the engine's queue does
/// the waiting, so there is no timer and no state here.
/// </summary>
internal sealed class FaultScheduler : IHandler
{
    public static readonly Address Address = new(ActorKind.FaultScheduler);

    Address IHandler.Address => Address;

    public SimTime? NextWakeUp => null;

    public void Handle(Message message, SimTime now, Outbox outbox)
    {
        if (message is ScheduleFault { Command: var c })
            outbox.Send(Domain.Control.Address.Elevator(c.ElevatorId), new ApplyFault(c), Math.Max(0, c.At.Ms - now.Ms));
    }
}
