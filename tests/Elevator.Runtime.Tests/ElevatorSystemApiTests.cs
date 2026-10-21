using Elevator.Domain;

namespace Elevator.Runtime.Tests;

/// <summary>The spec's ElevatorSystem API on the running actor system.</summary>
public class ElevatorSystemApiTests
{
    [Fact]
    public async Task Find_best_elevator_answers_without_assigning_and_assign_request_then_assigns()
    {
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Floors: 20, Elevators: 3, Dispatch: "nearest", Speed: 50));
        var request = Request.Create(9, 14, system.Clock.Now, 20, id: 1).Value!;

        int? best = null;
        for (var i = 0; i < 100 && best is null; i++) // until every elevator has published its first snapshot
        {
            best = await system.FindBestElevator(request).WaitAsync(TimeSpan.FromSeconds(5));
            if (best is null) await Task.Delay(10);
        }
        Assert.Equal(0, best);               // all idle at the lobby: tie broken to the lowest id
        Assert.Equal(0, system.PendingAssignments);

        var assignment = await system.AssignRequest(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((1, 0), (assignment.RequestId, assignment.ElevatorId));
        Assert.Equal(0, system.RequestQueueLength);

        system.BalanceLoad(); // sticky by default: a no-op, and must not disturb anything
        Assert.Null(system.Fault);
    }
}
