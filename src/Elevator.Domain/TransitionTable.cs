namespace Elevator.Domain;

/// <summary>
/// The explicit transition table of the elevator (ADR-002). This is the single source of truth for legal
/// state changes: <see cref="Elevator"/> never assigns its state directly, it asks this table.
///
/// The safety invariant "never moves unless the doors are closed" is structural here: the only edges into
/// MovingUp/MovingDown leave from Idle, and Idle is only reached from DoorClosing via DoorsFullyClosed
/// (or from Maintenance, which is entered from Idle). There is no edge from any door state to a moving state.
/// </summary>
public static class TransitionTable
{
    private static readonly Dictionary<(ElevatorState From, Trigger Trigger), ElevatorState> Table = new()
    {
        [(ElevatorState.Idle, Trigger.StartMoveUp)] = ElevatorState.MovingUp,
        [(ElevatorState.Idle, Trigger.StartMoveDown)] = ElevatorState.MovingDown,
        [(ElevatorState.Idle, Trigger.ArriveAndOpen)] = ElevatorState.DoorOpening,
        [(ElevatorState.MovingUp, Trigger.ArriveAndOpen)] = ElevatorState.DoorOpening,
        [(ElevatorState.MovingDown, Trigger.ArriveAndOpen)] = ElevatorState.DoorOpening,
        [(ElevatorState.DoorOpening, Trigger.DoorsFullyOpen)] = ElevatorState.DoorOpen,
        [(ElevatorState.DoorOpen, Trigger.StartClosing)] = ElevatorState.DoorClosing,
        [(ElevatorState.DoorClosing, Trigger.DoorsFullyClosed)] = ElevatorState.Idle,
        [(ElevatorState.DoorClosing, Trigger.Reopen)] = ElevatorState.DoorOpening,
        [(ElevatorState.Idle, Trigger.EnterMaintenance)] = ElevatorState.Maintenance,
        [(ElevatorState.Maintenance, Trigger.ExitMaintenance)] = ElevatorState.Idle,
    };

    public static IReadOnlyDictionary<(ElevatorState From, Trigger Trigger), ElevatorState> Transitions => Table;

    public static bool TryNext(ElevatorState from, Trigger trigger, out ElevatorState next) =>
        Table.TryGetValue((from, trigger), out next);

    /// <exception cref="IllegalTransitionException">The pair is not in the table. This is a programming error, not bad input.</exception>
    public static ElevatorState Next(ElevatorState from, Trigger trigger) =>
        TryNext(from, trigger, out var next) ? next : throw new IllegalTransitionException(from, trigger);

    /// <summary>Doors are closed in every state that is not part of the door cycle.</summary>
    public static bool DoorsClosed(ElevatorState state) =>
        state is ElevatorState.Idle or ElevatorState.MovingUp or ElevatorState.MovingDown or ElevatorState.Maintenance;
}

public sealed class IllegalTransitionException(ElevatorState from, Trigger trigger)
    : InvalidOperationException($"Illegal transition: {from} --{trigger}--> (not in transition table)")
{
    public ElevatorState From { get; } = from;
    public Trigger Trigger { get; } = trigger;
}
