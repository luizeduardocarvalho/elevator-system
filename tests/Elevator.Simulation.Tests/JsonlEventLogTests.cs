using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.EventLog;

namespace Elevator.Simulation.Tests;

public class JsonlEventLogTests
{
    [Fact]
    public void Every_event_type_round_trips()
    {
        var t = new Instant(1_234);
        DomainEvent[] events =
        [
            new RunStarted(t, "s", 7, 10, "look", 3),
            new PassengerArrived(t, 1, 2, 3),
            new HallCallRegistered(t, 2, Direction.Up),
            new HallCallAnswered(t, 0, 2, Direction.Up),
            new CarCallRegistered(t, 0, 3),
            new RequestRejected(t, "HallCall", 1, Direction.Down, "no DOWN button"),
            new RequestRejected(t, "CarCall", 11, null, "out of range"),
            new StateChanged(t, 0, ElevatorState.Idle, ElevatorState.MovingUp, Trigger.StartMoveUp, 2),
            new FloorReached(t, 0, 3),
            new DoorsOpened(t, 0, 3, null),
            new PassengerBoarded(t, 1, 0, 2),
            new PassengerAlighted(t, 1, 0, 3),
            new RunEnded(t, 1, 0),
            new RequestSubmitted(t, 5, 1, 9),
            new AssignmentProposed(t, 5, 2, 12.5, 30_000, false),
            new AssignmentAccepted(t, 5, 2),
            new AssignmentRejected(t, 5, 1, AssignmentReason.Stale),
            new AssignmentReleased(t, 5, 2, AssignmentReason.Fault),
            new DispatchDeferred(t, 5),
            new DuplicateSubmission(t, 5),
            new PassengerLeftBehind(t, 5, 2, 1),
            new OverloadModeChanged(t, true, 201),
        ];

        using var writer = new StringWriter();
        JsonlEventLog.Write(writer, events);
        var read = JsonlEventLog.Read(new StringReader(writer.ToString())).ToList();

        Assert.Equal(events, read);
    }

    [Fact]
    public void Line_format_uses_seq_type_t_and_spec_enum_spelling()
    {
        var line = JsonlEventLog.Serialize(
            new StateChanged(new Instant(8_000), 0, ElevatorState.MovingUp, ElevatorState.DoorOpening, Trigger.ArriveAndOpen, 5), 42);

        Assert.Equal(
            """{"seq":42,"type":"StateChanged","t":8000,"elevatorId":0,"from":"MOVING_UP","to":"DOOR_OPENING","trigger":"ARRIVE_AND_OPEN","floor":5}""",
            line);
    }

    /// <summary>
    /// ADR-019 renamed SimulationStarted/Ended to RunStarted/Ended in code only. Lines from a log written before the
    /// rename (v1-basic, seed 1) still read, and write back byte-identically.
    /// </summary>
    [Theory]
    [InlineData("""{"seq":0,"type":"SimulationStarted","t":0,"scenario":"v1-basic","seed":1,"floors":10,"scheduling":"look","passengers":3,"elevators":1,"dispatch":"cost","capacity":10,"bypassThreshold":0.8,"capacityMode":"next-trip"}""", typeof(RunStarted))]
    [InlineData("""{"seq":80,"type":"SimulationEnded","t":62000,"delivered":3,"unserved":0}""", typeof(RunEnded))]
    public void Logs_written_before_the_rename_still_replay(string line, Type expected)
    {
        var e = JsonlEventLog.Deserialize(line);

        Assert.IsType(expected, e);
        var seq = long.Parse(line[7..line.IndexOf(',')], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(line, JsonlEventLog.Serialize(e, seq));
    }
}
