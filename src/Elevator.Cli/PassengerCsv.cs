using System.Globalization;
using Elevator.Domain;
using Elevator.Metrics;

namespace Elevator.Cli;

internal static class PassengerCsv
{
    public static void Write(string path, IReadOnlyList<PassengerRecord> records)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        using var w = new StreamWriter(path) { NewLine = "\n" };
        var buckets = Enum.GetValues<WaitBucket>();
        w.WriteLine("id,from,to,t_call,t_first_assigned,elevator,assignments,rejections,left_behind,t_elevator_arrived,t_board,t_alight,wait_s,ride_s,journey_s,"
                    + string.Join(',', buckets.Select(b => "wait_" + BenchAnalysis.BucketName(b) + "_s")));
        foreach (var r in records)
        {
            string[] cells =
            [
                I(r.Id), I(r.From), I(r.To), T(r.CalledAt), T(r.FirstAssignedAt), r.Elevator is { } e ? I(e) : "",
                I(r.Assignments), I(r.Rejections), I(r.LeftBehind), T(r.ElevatorArrivedAt), T(r.BoardedAt), T(r.AlightedAt),
                D(r.WaitS), D(r.RideS), D(r.JourneyS), .. buckets.Select(b => D(r.WaitBuckets[b])),
            ];
            w.WriteLine(string.Join(',', cells));
        }
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string T(Instant? t) => t is { } x ? x.Seconds.ToString("0.000", CultureInfo.InvariantCulture) : "";

    private static string D(double? v) => v is { } x ? x.ToString("0.000", CultureInfo.InvariantCulture) : "";
}
