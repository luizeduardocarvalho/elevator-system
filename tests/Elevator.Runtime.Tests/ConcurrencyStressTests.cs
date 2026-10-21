using Elevator.Domain;
using Elevator.Domain.Events;

namespace Elevator.Runtime.Tests;

public class ConcurrencyStressTests
{
    /// <summary>
    /// Many threads released at the same instant by a <see cref="Barrier"/>, round after round, all submitting at once.
    /// Every submission must be assigned exactly once, no request may be owned by two elevators, and every elevator's
    /// state changes must form one valid chain (single writer).
    /// </summary>
    [Theory]
    [InlineData("nearest", 32, 300)]
    [InlineData("cost", 16, 60)]
    public async Task Simultaneous_submissions_are_each_assigned_exactly_once(string dispatch, int threads, int rounds)
    {
        var options = new RuntimeOptions(Floors: 20, Elevators: 4, Capacity: 1_000, BypassThreshold: 1.0, Dispatch: dispatch, Speed: 200);
        var system = ElevatorSystem.Start(options);
        var assignments = new Assignment[threads * rounds];
        var nextId = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        using (var barrier = new Barrier(threads))
        {
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                var rng = new Random(t);
                try
                {
                    for (var round = 0; round < rounds; round++)
                    {
                        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30))) throw new TimeoutException("barrier");
                        var id = Interlocked.Increment(ref nextId);
                        var from = rng.Next(1, 21);
                        var to = from == 20 ? 1 : rng.Next(from + 1, 21);
                        var request = Request.Create(from, to, system.Clock.Now, 20, id: id).Value!;
                        // A plain thread blocking on its own submission, as a synchronous caller would.
                        assignments[id - 1] = system.AssignRequest(request).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                    barrier.RemoveParticipant(); // let the others finish instead of waiting forever
                }
            })).ToList();
            workers.ForEach(w => w.Start());
            workers.ForEach(w => w.Join());
        }

        await system.DisposeAsync();
        Assert.Null(system.Fault);
        Assert.Empty(failures);
        Assert.All(assignments, Assert.NotNull);
        Assert.Equal(threads * rounds, assignments.Select(a => a.RequestId).Distinct().Count());

        var events = system.Events;
        Assert.Equal(threads * rounds, events.OfType<RequestSubmitted>().Count());
        Assert.Empty(events.OfType<DuplicateSubmission>());
        Invariants.Check(events, options.Capacity);
    }
}
