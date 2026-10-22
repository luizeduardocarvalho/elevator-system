using Elevator.Domain.Events;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Domain.Tests;

/// <summary>
/// Property tests: random interleavings of hall calls, car calls and time advances, checking safety invariants
/// on the produced event stream after every step.
/// </summary>
public class ElevatorInvariantProperties
{
    public abstract record Op;
    public sealed record HallOp(int Floor, Direction Direction) : Op;
    public sealed record CarOp(int Floor) : Op;
    public sealed record AdvanceOp(int Ms) : Op;

    // Floors 0 and 11 are included on purpose: an invalid call must be refused (a precondition the shells check first,
    // ADR-017) without breaking anything.
    private static readonly Gen<Op> OpGen = Gen.OneOf(
        from f in Gen.Choose(0, Harness.Floors + 1)
        from d in Gen.Elements(Direction.Up, Direction.Down)
        select (Op)new HallOp(f, d),
        Gen.Choose(0, Harness.Floors + 1).Select(f => (Op)new CarOp(f)),
        Gen.Choose(0, 9_000).Select(ms => (Op)new AdvanceOp(ms)));

    private static Arbitrary<Op[]> Scripts => Gen.ArrayOf(OpGen).ToArbitrary();

    [Property(MaxTest = 300)]
    public Property Safety_invariants_hold_for_any_call_sequence() => Prop.ForAll(Scripts, ops =>
    {
        var h = new Harness();
        long now = 0;
        foreach (var op in ops)
        {
            if (op is AdvanceOp adv) h.At(now += adv.Ms);
            else Call(h, now, op);
            CheckInvariants(h);
        }
    });

    [Property(MaxTest = 200)]
    public Property Every_valid_call_is_eventually_cleared() => Prop.ForAll(Scripts, ops =>
    {
        var h = new Harness();
        long now = 0;
        foreach (var op in ops)
        {
            if (op is AdvanceOp adv) h.At(now += adv.Ms);
            else Call(h, now, op);
        }

        // With no new input, a finite amount of work must drain: ≤ 2 hall calls + 1 car call per floor,
        // each costing at most a full sweep plus a door cycle.
        h.At(now + 3L * Harness.Floors * (Harness.Floors * 2_000 + 5_000));
        Assert.Empty(h.Elevator.HallCalls);
        Assert.Empty(h.Elevator.CarCalls);
        Assert.Equal(ElevatorState.Idle, h.Elevator.State);
    });

    /// <summary>Registers a call; an invalid one must throw and leave the elevator as it was.</summary>
    private static void Call(Harness h, long now, Op op)
    {
        var (valid, register) = op switch
        {
            HallOp hall => (Floors.CheckHallButton(hall.Floor, hall.Direction, Harness.Floors) is null, () => h.Hall(now, hall.Floor, hall.Direction)),
            CarOp car => (Floors.Check(car.Floor, Harness.Floors) is null, (Action)(() => h.Car(now, car.Floor))),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        if (valid)
        {
            register();
            return;
        }
        var targets = h.Elevator.TargetFloors;
        Assert.Throws<ArgumentOutOfRangeException>(register);
        Assert.Equal(targets, h.Elevator.TargetFloors);
    }

    private static void CheckInvariants(Harness h)
    {
        var state = ElevatorState.Idle;
        var floor = 1;
        foreach (var e in h.Events)
        {
            switch (e)
            {
                case StateChanged sc:
                    Assert.Equal(state, sc.From);
                    Assert.Equal(TransitionTable.Next(sc.From, sc.Trigger), sc.To); // every change is a table edge
                    Assert.Equal(floor, sc.Floor);
                    state = sc.To;
                    break;
                case FloorReached fr:
                    // The floor only changes while moving, i.e. with doors closed, by exactly one in the travel direction.
                    Assert.True(state is ElevatorState.MovingUp or ElevatorState.MovingDown, $"moved while {state}");
                    Assert.True(TransitionTable.DoorsClosed(state));
                    Assert.Equal(state == ElevatorState.MovingUp ? floor + 1 : floor - 1, fr.Floor);
                    floor = fr.Floor;
                    break;
                case DoorsOpened d:
                    Assert.Equal(ElevatorState.DoorOpen, state);
                    Assert.Equal(floor, d.Floor);
                    break;
                case HallCallAnswered a:
                    Assert.Equal(ElevatorState.DoorOpen, state);
                    Assert.Equal(floor, a.Floor);
                    break;
            }
            Assert.InRange(floor, 1, Harness.Floors);
        }

        Assert.Equal(state, h.Elevator.State);
        Assert.Equal(floor, h.Elevator.CurrentFloor);
        Assert.InRange(h.Elevator.CurrentFloor, 1, Harness.Floors);
        Assert.All(h.Elevator.HallCalls, c => Assert.Null(Floors.CheckHallButton(c.Floor, c.Direction, Harness.Floors)));
    }
}
