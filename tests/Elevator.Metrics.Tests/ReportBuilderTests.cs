using Elevator.Domain;
using Elevator.Domain.Events;

namespace Elevator.Metrics.Tests;

public class ReportBuilderTests
{
    private static Instant S(double seconds) => Instant.FromSeconds(seconds);

    [Fact]
    public void Builds_report_from_events_only()
    {
        DomainEvent[] events =
        [
            new RunStarted(S(0), "synthetic", 9, 10, "look", 3),
            new PassengerArrived(S(0), 1, 1, 5),
            new PassengerArrived(S(2), 2, 4, 2),
            new PassengerArrived(S(3), 3, 6, 1),
            new RequestRejected(S(3), "CarCall", 12, null, "out of range"),
            new PassengerBoarded(S(1), 1, 0, 1),
            new FloorReached(S(3), 0, 2),
            new DoorsOpened(S(9), 0, 5, null),
            new PassengerAlighted(S(9), 1, 0, 5),
            new PassengerBoarded(S(12), 2, 0, 4),
            new DoorsOpened(S(16), 0, 2, null),
            new PassengerAlighted(S(16), 2, 0, 2),
            new RunEnded(S(20), 2, 1),
        ];

        var report = ReportBuilder.Build(events);

        Assert.Equal(("synthetic", "look", 9), (report.Scenario, report.Scheduling, report.Seed));
        Assert.Equal((3, 2, 1, 1), (report.Passengers, report.Delivered, report.Unserved, report.RejectedRequests));
        Assert.Equal(new DurationStats(2, 5.5, 10, 10), report.Wait);
        Assert.Equal(new DurationStats(2, 6, 8, 8), report.Ride);
        Assert.Equal((1, 2, 20.0), (report.FloorsTravelled, report.Stops, report.SimTimeS));
    }

    [Fact]
    public void Empty_run_has_no_stats() =>
        Assert.Equal(new DurationStats(0, null, null, null), ReportBuilder.Build([]).Wait);
}
