namespace Elevator.Domain;

/// <summary>A pickup stop as seen by a fast scheduling decision: floor, direction, and when it was registered.</summary>
public readonly record struct PickupRef(int Floor, Direction Direction, long RegisteredAtMs);

/// <summary>
/// Compact view of an elevator's stops for O(1) scheduling decisions: drop-offs and pickups as 64-bit floor masks
/// (bit f = floor f), plus the pickups in registration order for the rules that need order (FIFO's oldest call,
/// LOOK's idle tie-break). Only usable with at most 63 floors.
/// </summary>
public readonly ref struct StopSet(int floor, ElevatorState state, Direction? committed, ulong carCalls, ulong upPickups, ulong downPickups, ReadOnlySpan<PickupRef> pickups)
{
    public const int MaxFloors = 63;

    public int Floor { get; } = floor;

    public ElevatorState State { get; } = state;

    public Direction? CommittedDirection { get; } = committed;

    public ulong CarCalls { get; } = carCalls;

    public ulong UpPickups { get; } = upPickups;

    public ulong DownPickups { get; } = downPickups;

    /// <summary>Pickups in registration order (oldest first).</summary>
    public ReadOnlySpan<PickupRef> Pickups { get; } = pickups;

    public bool IsEmpty => CarCalls == 0 && Pickups.IsEmpty;

    /// <summary>Bits strictly above floor f.</summary>
    public static ulong Above(int f) => f >= 63 ? 0 : ~0UL << (f + 1);

    /// <summary>Bits strictly below floor f.</summary>
    public static ulong Below(int f) => (1UL << f) - 1;

    public static ulong Bit(int f) => 1UL << f;
}

/// <summary>
/// A scheduling policy that can also decide from a <see cref="StopSet"/> in O(1) (bit operations). Must return
/// exactly what <see cref="ISchedulingPolicy.Decide"/> returns for the same state; a property test checks it.
/// Route projections use it so that a dispatch decision costs O(floors) per elevator (ADR-006).
/// </summary>
public interface IFastSchedulingPolicy : ISchedulingPolicy
{
    PolicyDecision Decide(in StopSet stops);
}
