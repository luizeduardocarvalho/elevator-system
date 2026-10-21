using Elevator.Domain;
using ElevatorUnit = Elevator.Domain.Elevator;

namespace Elevator.Policy.Tests;

/// <summary>
/// The cost of a dispatch decision must not grow with the backlog (ADR-006). Timing is measured with
/// BenchmarkDotNet (docs/results.md); here the deterministic work units are pinned: policy decisions made by the
/// per-stop projection, which are bounded by the number of stops (two per floor), not by the people queued.
/// </summary>
public class ProjectionScalingTests
{
    private static int StepsFor(int backlog)
    {
        var look = new LookScheduling();
        var rng = new Random(42);
        var e = new ElevatorUnit(0, 20, ElevatorTiming.Default, look, 1, capacity: 100_000, bypassThreshold: 1.0);
        for (var id = 1; id <= backlog; id++)
        {
            var from = rng.Next(1, 21);
            int to;
            do to = rng.Next(1, 21); while (to == from);
            e.AssignPickup(Request.Create(from, to, SimTime.Zero, 20, id: id).Value!);
        }
        e.Tick(new SimTime(3_000));
        var projection = new StopProjection(look, ElevatorTiming.Default);
        var extra = Request.Create(7, 15, new SimTime(3_000), 20, id: 1_000_000).Value!;
        return projection.Project(e.Snapshot(), extra, new SimTime(3_000)).Steps + projection.Project(e.Snapshot(), null, new SimTime(3_000)).Steps;
    }

    [Fact]
    public void Work_per_decision_is_flat_in_the_backlog()
    {
        var steps = new[] { 25, 100, 400, 1_600 }.ToDictionary(b => b, StepsFor);

        // Once every floor has stops in both directions (around 100 queued), more people add essentially no work:
        // the few decisions of difference come from where the extra request lands in the sweep.
        Assert.InRange(steps[1_600], steps[400] * 0.9, steps[400] * 1.1);
        Assert.InRange(steps[1_600], 1, steps[100] * 1.25);
        Assert.True(steps[1_600] < 20 * 2 * 10, $"steps {steps[1_600]} not bounded by floors"); // a few decisions per stop, 2 stops per floor
    }
}
