using Elevator.Domain.Events;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Domain.Tests;

public class ElevatorInvariantProperties
{
    public abstract record Op;
    public sealed record HallOp(int Floor, Direction Direction) : Op;
    public sealed record CarOp(int Floor) : Op;
    public sealed record AdvanceOp(int Ms) : Op;

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
            switch (op)
            {
                case HallOp hall: h.Hall(now, hall.Floor, hall.Direction); break;
                case CarOp car: h.Car(now, car.Floor); break;
                case AdvanceOp adv: now += adv.Ms; h.At(now); break;
            }
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
            if (op is HallOp hall) h.Hall(now, hall.Floor, hall.Direction);
            else if (op is CarOp car) h.Car(now, car.Floor);
            else if (op is AdvanceOp adv) h.At(now += adv.Ms);
        }

        h.At(now + 3L * Harness.Floors * (Harness.Floors * 2_000 + 5_000));
        Assert.Empty(h.Elevator.HallCalls);
        Assert.Empty(h.Elevator.CarCalls);
        Assert.Equal(ElevatorState.Idle, h.Elevator.State);
    });

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
                    Assert.Equal(TransitionTable.Next(sc.From, sc.Trigger), sc.To);
                    Assert.Equal(floor, sc.Floor);
                    state = sc.To;
                    break;
                case FloorReached fr:
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
