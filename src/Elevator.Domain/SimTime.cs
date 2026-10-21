namespace Elevator.Domain;

/// <summary>
/// Simulated time: integer milliseconds since the start of a run. A distinct type so it can never be mixed
/// with wall-clock time (ADR-003). Integers keep runs bit-for-bit reproducible.
/// </summary>
public readonly record struct SimTime(long Ms) : IComparable<SimTime>
{
    public static readonly SimTime Zero = new(0);

    public static SimTime FromSeconds(double seconds) =>
        new((long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));

    public double Seconds => Ms / 1000.0;

    public SimTime AddMs(long ms) => new(Ms + ms);

    public int CompareTo(SimTime other) => Ms.CompareTo(other.Ms);

    public static bool operator <(SimTime a, SimTime b) => a.Ms < b.Ms;
    public static bool operator >(SimTime a, SimTime b) => a.Ms > b.Ms;
    public static bool operator <=(SimTime a, SimTime b) => a.Ms <= b.Ms;
    public static bool operator >=(SimTime a, SimTime b) => a.Ms >= b.Ms;

    public static SimTime Min(SimTime a, SimTime b) => a <= b ? a : b;

    public override string ToString() => $"{Seconds:0.000}s";
}
