using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;

namespace Elevator.Runtime.Tests;

public class LogCausalityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(200_000)]
    public async Task Every_assign_request_logs_its_passenger_before_anything_about_the_request(int busyPassengers)
    {
        const int threads = 16, each = 50;
        await using var system = ElevatorSystem.Start(new RuntimeOptions(Speed: 50, Dispatch: "nearest"));
        var noOp = new ElevatorNotice(new DoorsOpened(SimTime.Zero, 0, 20, null));
        for (var i = 0; i < busyPassengers; i++) system.Post(Address.Passengers, noOp);
        var rng = new Random(5);
        var requests = Enumerable.Range(0, threads * each).Select(i =>
        {
            var from = rng.Next(1, 21);
            int to;
            do to = rng.Next(1, 21); while (to == from);
            return Request.Create(from, to, SimTime.Zero, 20, id: i + 1).Value!;
        }).ToList();

        using var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(async () =>
        {
            gate.Wait();
            for (var i = t * each; i < (t + 1) * each; i++) await system.AssignRequest(requests[i]);
        })).ToList();
        gate.Set();
        await Task.WhenAll(tasks);
        await Task.Delay(200);

        var firstSeq = new Dictionary<(Type, int), int>();
        var events = system.Events;
        for (var seq = 0; seq < events.Count; seq++)
        {
            var key = events[seq] switch
            {
                PassengerArrived a => (typeof(PassengerArrived), a.PassengerId),
                RequestSubmitted s => (typeof(RequestSubmitted), s.RequestId),
                AssignmentProposed p => (typeof(AssignmentProposed), p.RequestId),
                AssignmentAccepted a => (typeof(AssignmentAccepted), a.RequestId),
                _ => (null as Type, 0),
            };
            if (key.Item1 is not null) firstSeq.TryAdd(key!, seq);
        }

        var violations = requests.Where(r =>
        {
            int Seq(Type t) => firstSeq.TryGetValue((t, r.Id), out var s) ? s : int.MaxValue;
            return !(Seq(typeof(PassengerArrived)) < Seq(typeof(RequestSubmitted))
                     && Seq(typeof(RequestSubmitted)) < Seq(typeof(AssignmentProposed))
                     && Seq(typeof(AssignmentProposed)) < Seq(typeof(AssignmentAccepted))
                     && Seq(typeof(AssignmentAccepted)) != int.MaxValue);
        }).Select(r => r.Id).ToList();
        Assert.True(violations.Count == 0, $"{violations.Count} requests out of causal order, e.g. {string.Join(", ", violations.Take(5))}");
    }
}
