namespace Elevator.Domain.Tests;

public class TransitionTableTests
{
    // Written out by hand on purpose: the test is an independent statement of the intended machine,
    // not a copy of TransitionTable's dictionary.
    public static readonly TheoryData<ElevatorState, Trigger, ElevatorState> Legal = new()
    {
        { ElevatorState.Idle, Trigger.StartMoveUp, ElevatorState.MovingUp },
        { ElevatorState.Idle, Trigger.StartMoveDown, ElevatorState.MovingDown },
        { ElevatorState.Idle, Trigger.ArriveAndOpen, ElevatorState.DoorOpening },
        { ElevatorState.MovingUp, Trigger.ArriveAndOpen, ElevatorState.DoorOpening },
        { ElevatorState.MovingDown, Trigger.ArriveAndOpen, ElevatorState.DoorOpening },
        { ElevatorState.DoorOpening, Trigger.DoorsFullyOpen, ElevatorState.DoorOpen },
        { ElevatorState.DoorOpen, Trigger.StartClosing, ElevatorState.DoorClosing },
        { ElevatorState.DoorClosing, Trigger.DoorsFullyClosed, ElevatorState.Idle },
        { ElevatorState.DoorClosing, Trigger.Reopen, ElevatorState.DoorOpening },
        { ElevatorState.Idle, Trigger.EnterMaintenance, ElevatorState.Maintenance },
        { ElevatorState.Maintenance, Trigger.ExitMaintenance, ElevatorState.Idle },
        // Emergency stop (ADR-013): halt at the next floor with the doors closed; doors in a cycle close and stay closed.
        { ElevatorState.MovingUp, Trigger.EmergencyHalt, ElevatorState.Idle },
        { ElevatorState.MovingDown, Trigger.EmergencyHalt, ElevatorState.Idle },
        { ElevatorState.DoorOpening, Trigger.EmergencyClose, ElevatorState.DoorClosing },
        { ElevatorState.DoorOpen, Trigger.EmergencyClose, ElevatorState.DoorClosing },
    };

    public static TheoryData<ElevatorState, Trigger> Illegal()
    {
        var legal = Legal.Select(row => ((ElevatorState)row[0], (Trigger)row[1])).ToHashSet();
        var data = new TheoryData<ElevatorState, Trigger>();
        foreach (var state in Enum.GetValues<ElevatorState>())
        foreach (var trigger in Enum.GetValues<Trigger>())
            if (!legal.Contains((state, trigger)))
                data.Add(state, trigger);
        return data;
    }

    [Theory]
    [MemberData(nameof(Legal))]
    public void Legal_transition_goes_to_expected_state(ElevatorState from, Trigger trigger, ElevatorState to) =>
        Assert.Equal(to, TransitionTable.Next(from, trigger));

    [Theory]
    [MemberData(nameof(Illegal))]
    public void Illegal_transition_throws(ElevatorState from, Trigger trigger)
    {
        var ex = Assert.Throws<IllegalTransitionException>(() => TransitionTable.Next(from, trigger));
        Assert.Equal(from, ex.From);
        Assert.Equal(trigger, ex.Trigger);
    }

    [Fact]
    public void Table_contains_exactly_the_legal_transitions() =>
        Assert.Equal(Legal.Count(), TransitionTable.Transitions.Count);

    [Fact]
    public void Moving_states_are_only_entered_from_idle_with_doors_closed()
    {
        var intoMoving = TransitionTable.Transitions
            .Where(t => t.Value is ElevatorState.MovingUp or ElevatorState.MovingDown)
            .Select(t => t.Key.From);
        Assert.All(intoMoving, from => Assert.Equal(ElevatorState.Idle, from));
        Assert.True(TransitionTable.DoorsClosed(ElevatorState.Idle));
    }

    [Fact]
    public void Every_state_is_reachable_and_has_an_exit()
    {
        foreach (var state in Enum.GetValues<ElevatorState>())
        {
            Assert.Contains(TransitionTable.Transitions, t => t.Key.From == state);
            Assert.True(state == ElevatorState.Idle || TransitionTable.Transitions.Any(t => t.Value == state), $"{state} unreachable");
        }
    }

    [Fact]
    public void Elevator_rejects_spec_method_called_in_wrong_state()
    {
        var h = new Harness();
        Assert.Throws<IllegalTransitionException>(() => h.Elevator.CloseDoor()); // Idle: doors already closed
        h.Elevator.MoveUp();
        Assert.Throws<IllegalTransitionException>(() => h.Elevator.MoveDown()); // no reversing mid-flight
    }
}
