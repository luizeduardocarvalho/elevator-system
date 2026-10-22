using System.Globalization;
using System.Numerics;

namespace Elevator.Domain;

/// <param name="ServedFloors">Bit f = floor f (<see cref="FloorMask"/>); <see cref="FloorMask.All"/> = every floor.</param>
public sealed record ElevatorType(string Name, ElevatorTiming Timing, int Capacity, ulong ServedFloors = FloorMask.All)
{
    public const string StandardName = "standard";

    public static ElevatorType Standard(ElevatorTiming timing, int capacity) => new(StandardName, timing, capacity);

    public bool Serves(int floor) => FloorMask.Contains(ServedFloors, floor);

    /// <summary>The lowest served floor of a building of <paramref name="floors"/> floors: where this elevator starts.</summary>
    public int LowestServed(int floors) => Enumerable.Range(Floors.Lowest, floors).First(Serves);
}

/// <summary>
/// Sets of floors as 64-bit masks (bit f = floor f), the encoding of <see cref="ElevatorSnapshot.ServedFloors"/>.
/// <see cref="All"/> is special: it means every floor, also above 63, so a building without types has no floor limit.
/// Restricted sets (types with served floors) need floors ≤ 63; the scenario loader checks it.
/// </summary>
public static class FloorMask
{
    public const ulong All = ulong.MaxValue;

    public const int MaxFloor = 63;

    public static bool Contains(ulong mask, int floor) => mask == All || (floor is >= 0 and <= MaxFloor && (mask & (1UL << floor)) != 0);

    public static ulong Of(IEnumerable<int> floors) => floors.Aggregate(0UL, (m, f) => m | (1UL << f));

    /// <summary>Parses entries like "1", "10-20" into a mask; floors must be in 1..<paramref name="floors"/> and ≤ 63.</summary>
    public static Result<ulong> Parse(IEnumerable<string> entries, int floors)
    {
        var mask = 0UL;
        foreach (var raw in entries)
        {
            var text = raw.Trim();
            var parts = text.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                return Result<ulong>.Fail($"'{text}' is not a floor or a range like 10-20");
            var from = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var to = int.Parse(parts[^1], CultureInfo.InvariantCulture);
            if (from > to) return Result<ulong>.Fail($"range '{text}' is reversed");
            if ((Floors.Check(from, floors) ?? Floors.Check(to, floors)) is { } error)
                return Result<ulong>.Fail($"'{text}': {error}");
            if (to > MaxFloor) return Result<ulong>.Fail($"'{text}': served floors are limited to floors 1..{MaxFloor}");
            for (var f = from; f <= to; f++) mask |= 1UL << f;
        }
        return mask == 0 ? Result<ulong>.Fail("no floor is served") : Result<ulong>.Ok(mask);
    }

    /// <summary>"1, 10-20" style; "all" for <see cref="All"/>.</summary>
    public static string Describe(ulong mask, int floors)
    {
        if (mask == All) return "all";
        var ranges = new List<string>();
        for (var f = Floors.Lowest; f <= Math.Min(floors, MaxFloor); f++)
        {
            if (!Contains(mask, f)) continue;
            var end = f;
            while (end + 1 <= Math.Min(floors, MaxFloor) && Contains(mask, end + 1)) end++;
            ranges.Add(end == f ? f.ToString(CultureInfo.InvariantCulture) : $"{f}-{end}");
            f = end;
        }
        return string.Join(", ", ranges);
    }

    public static int Count(ulong mask) => BitOperations.PopCount(mask);
}
