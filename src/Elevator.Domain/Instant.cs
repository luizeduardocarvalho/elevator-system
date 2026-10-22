namespace Elevator.Domain;

/// <summary>
/// A point in a run's time: integer milliseconds since the run started. The simulator advances it explicitly; the
/// runtime derives it from the wall clock (<c>RealClock</c>). A distinct type so it can never be mixed with wall-clock
/// durations (ADR-003, ADR-008). Integers keep runs bit-for-bit reproducible. Named <c>SimTime</c> before ADR-019.
/// </summary>
public readonly record struct Instant(long Ms) : IComparable<Instant>
{
    public static readonly Instant Zero = new(0);

    public static Instant FromSeconds(double seconds) =>
        new((long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));

    public double Seconds => Ms / 1000.0;

    public Instant AddMs(long ms) => new(Ms + ms);

    public int CompareTo(Instant other) => Ms.CompareTo(other.Ms);

    public static bool operator <(Instant a, Instant b) => a.Ms < b.Ms;
    public static bool operator >(Instant a, Instant b) => a.Ms > b.Ms;
    public static bool operator <=(Instant a, Instant b) => a.Ms <= b.Ms;
    public static bool operator >=(Instant a, Instant b) => a.Ms >= b.Ms;

    public static Instant Min(Instant a, Instant b) => a <= b ? a : b;

    public override string ToString() => $"{Seconds:0.000}s";
}
