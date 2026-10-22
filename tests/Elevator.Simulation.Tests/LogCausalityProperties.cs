using Elevator.Domain.Events;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Elevator.Simulation.Tests;

public class LogCausalityProperties
{
    [Property(MaxTest = 80)]
    public Property Every_request_reads_in_causal_order() => Prop.ForAll(MultiElevatorProperties.CaseGen().ToArbitrary(), c =>
    {
        var log = SimEngine.Run(c.Scenario, c.Strategy).Events;

        for (var i = 1; i < log.Count; i++)
            Assert.True(log[i].At >= log[i - 1].At, $"time went backwards at seq {i}: {log[i - 1]} then {log[i]}");

        var arrived = new HashSet<int>();
        var submitted = new HashSet<int>();
        var proposed = new HashSet<int>();
        var accepted = new HashSet<int>();
        var boarded = new HashSet<int>();
        foreach (var e in log)
        {
            switch (e)
            {
                case PassengerArrived a:
                    arrived.Add(a.PassengerId);
                    break;
                case RequestSubmitted s:
                    Assert.Contains(s.RequestId, arrived);
                    submitted.Add(s.RequestId);
                    break;
                case AssignmentProposed p:
                    Assert.Contains(p.RequestId, submitted);
                    proposed.Add(p.RequestId);
                    break;
                case AssignmentAccepted a:
                    Assert.Contains(a.RequestId, proposed);
                    accepted.Add(a.RequestId);
                    break;
                case PassengerBoarded b:
                    Assert.Contains(b.PassengerId, accepted);
                    boarded.Add(b.PassengerId);
                    break;
                case PassengerAlighted al:
                    Assert.Contains(al.PassengerId, boarded);
                    break;
            }
        }
    });
}
