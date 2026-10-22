using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Tui.State;

namespace Elevator.Tui.Tests;

public class LogReaderTests
{
    [Fact]
    public void Known_events_round_trip()
    {
        DomainEvent[] events = [new FloorReached(new Instant(1_500), 2, 7), new PassengerArrived(new Instant(2_000), 4, 1, 9)];
        var items = LogReader.Read(new StringReader(Repo.ToJsonl(events)));
        Assert.Equal(events, items.Select(i => i.Known));
    }

    [Fact]
    public void Unknown_type_becomes_a_generic_item_with_its_fields()
    {
        var item = LogReader.Parse("""{"seq":9,"type":"ElevatorTeleported","t":4200,"elevatorId":3,"to":"ROOF","extra":{"a":1}}""", Instant.Zero);
        Assert.Null(item.Known);
        var u = item.Unknown!;
        Assert.Equal((new Instant(4_200), "ElevatorTeleported"), (u.At, u.Type));
        Assert.Equal(["elevatorId=3", "to=ROOF", "extra={\"a\":1}"], u.Fields.Select(f => $"{f.Key}={f.Value}"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    [InlineData("{\"type\":\"StateChanged\",\"t\":5,\"elevatorId\":\"oops\"}")]
    public void Garbage_never_throws(string line)
    {
        var item = LogReader.Parse(line, new Instant(77), lineNo: 3);
        Assert.NotNull(item.Unknown);
    }

    [Fact]
    public void Fields_of_a_known_event_use_the_log_spelling()
    {
        var fields = LogReader.FieldsOf(new StateChanged(new Instant(10), 1, ElevatorState.Idle, ElevatorState.MovingUp, Trigger.StartMoveUp, 3));
        Assert.Equal(["elevatorId=1", "from=IDLE", "to=MOVING_UP", "trigger=START_MOVE_UP", "floor=3"], fields.Select(f => $"{f.Key}={f.Value}"));
    }
}
