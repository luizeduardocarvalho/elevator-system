using Elevator.Domain;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Policy.Tests;

public class FastDecideEquivalenceTests
{
    public sealed record State(int Floors, int Floor, Direction? Dir, int[] CarCalls, (int Floor, Direction Dir, int At)[] Pickups);

    private static Gen<State> States =>
        from floors in Gen.Choose(2, 63)
        from floor in Gen.Choose(1, floors)
        from dir in Gen.Elements<Direction?>(null, Direction.Up, Direction.Down)
        from cars in Gen.ArrayOf(Gen.Choose(1, floors))
        from pickups in Gen.ArrayOf(from f in Gen.Choose(1, floors) from d in Gen.Elements(Direction.Up, Direction.Down) from at in Gen.Choose(0, 50) select (f, d, at))
        select new State(floors, floor, dir, cars, pickups);

    [Property(MaxTest = 3000)]
    public Property Look_bitmask_decide_equals_list_decide() => Same(new LookScheduling());

    private static Property Same(IFastSchedulingPolicy policy) => Prop.ForAll(States.ToArbitrary(), st =>
    {
        var carCalls = st.CarCalls.Distinct().Order().ToArray();
        var pickups = st.Pickups.DistinctBy(p => (p.Floor, p.Dir)).OrderBy(p => p.At).ToArray();
        var snapshot = ElevatorSnapshot.ForPolicy(st.Floor, ElevatorState.Idle, st.Dir, carCalls,
            pickups.Select(p => new HallCall(p.Floor, p.Dir, new Instant(p.At))).ToArray(), st.Floors);

        ulong cars = 0, up = 0, down = 0;
        foreach (var c in carCalls) cars |= StopSet.Bit(c);
        foreach (var p in pickups) if (p.Dir == Direction.Up) up |= StopSet.Bit(p.Floor); else down |= StopSet.Bit(p.Floor);
        var refs = pickups.Select(p => new PickupRef(p.Floor, p.Dir, p.At)).ToArray();

        Assert.Equal(policy.Decide(snapshot), policy.Decide(new StopSet(st.Floor, ElevatorState.Idle, st.Dir, cars, up, down, refs)));
    });
}
