namespace Elevator.Domain;

/// <summary>Direction enum from the spec. "No direction" is expressed as <c>Direction?</c>, never a new member.</summary>
public enum Direction
{
    Up,
    Down,
}

public static class Directions
{
    /// <summary>Direction of travel from <paramref name="from"/> to <paramref name="to"/>; null when equal.</summary>
    public static Direction? Between(int from, int to) =>
        to > from ? Direction.Up : to < from ? Direction.Down : null;

    /// <summary>True when <paramref name="floor"/> lies strictly ahead of <paramref name="origin"/> in <paramref name="direction"/>.</summary>
    public static bool IsAhead(this Direction direction, int origin, int floor) =>
        direction == Direction.Up ? floor > origin : floor < origin;
}
