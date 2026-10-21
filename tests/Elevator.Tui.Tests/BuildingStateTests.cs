using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Simulation;
using Elevator.Tui.State;

namespace Elevator.Tui.Tests;

public class BuildingStateTests
{
    private static SimTime S(double seconds) => SimTime.FromSeconds(seconds);

    private static BuildingState Fold(params DomainEvent[] events)
    {
        var state = new BuildingState();
        foreach (var e in events) state.Apply(e);
        return state;
    }

    private static SimulationStarted Started(int elevators = 2, int capacity = 10) =>
        new(SimTime.Zero, "t", 7, 10, "look", 3, elevators, "cost", capacity);

    [Fact]
    public void Started_sets_the_building_and_every_elevator_idle_at_the_lobby()
    {
        var s = Fold(Started(elevators: 3));
        Assert.Equal(("t", 7, 10, 3, 10), (s.Scenario, s.Seed, s.Floors, s.Elevators.Count, s.Capacity));
        Assert.All(s.Elevators, e => Assert.Equal((Floors.Lowest, ElevatorState.Idle, ElevatorMode.Normal, 0), (e.Floor, e.State, e.Mode, e.Riders)));
    }

    [Fact]
    public void Waiting_counts_follow_arrivals_and_boardings_and_waits_are_recorded()
    {
        var s = Fold(Started(),
            new PassengerArrived(S(1), 1, 3, 9), new PassengerArrived(S(2), 2, 3, 1), new PassengerArrived(S(2), 3, 3, 8),
            new PassengerBoarded(S(31), 1, 0, 3));
        Assert.Equal(new Dictionary<(int, Direction), int> { [(3, Direction.Up)] = 1, [(3, Direction.Down)] = 1 }, s.Waiting);
        Assert.Equal([30.0], s.Waits);
        Assert.Equal((3, 0, 1), (s.Arrived, s.Delivered, s.Elevators[0].Riders));

        s.Apply(new PassengerAlighted(S(40), 1, 0, 9));
        Assert.Equal((1, 0), (s.Delivered, s.Elevators[0].Riders));
    }

    [Fact]
    public void Left_behind_passengers_keep_waiting_and_a_re_arrival_is_not_counted_twice()
    {
        var s = Fold(Started(), new PassengerArrived(S(1), 1, 3, 9), new PassengerLeftBehind(S(20), 1, 0, 3), new PassengerArrived(S(21), 1, 3, 9));
        Assert.Equal(new Dictionary<(int, Direction), int> { [(3, Direction.Up)] = 1 }, s.Waiting);
        Assert.Equal(1, s.Arrived);
    }

    [Fact]
    public void Shown_direction_is_travel_when_moving_lantern_when_doors_open_or_closing_otherwise_none()
    {
        var s = Fold(Started(), new StateChanged(S(1), 0, ElevatorState.Idle, ElevatorState.MovingUp, Trigger.StartMoveUp, 1));
        Assert.Equal(Direction.Up, s.Elevators[0].ShownDirection);

        s.Apply(new FloorReached(S(3), 0, 2));
        s.Apply(new StateChanged(S(3), 0, ElevatorState.MovingUp, ElevatorState.DoorOpening, Trigger.ArriveAndOpen, 2));
        Assert.Equal((2, (Direction?)null), (s.Elevators[0].Floor, s.Elevators[0].ShownDirection));

        s.Apply(new StateChanged(S(4), 0, ElevatorState.DoorOpening, ElevatorState.DoorOpen, Trigger.DoorsFullyOpen, 2));
        s.Apply(new DoorsOpened(S(4), 0, 2, Direction.Down));
        Assert.Equal(Direction.Down, s.Elevators[0].ShownDirection);

        s.Apply(new HallCallAnswered(S(5), 0, 2, Direction.Up)); // adopted a caller's direction while open
        s.Apply(new StateChanged(S(7), 0, ElevatorState.DoorOpen, ElevatorState.DoorClosing, Trigger.StartClosing, 2));
        Assert.Equal(Direction.Up, s.Elevators[0].ShownDirection);

        s.Apply(new StateChanged(S(8), 0, ElevatorState.DoorClosing, ElevatorState.Idle, Trigger.DoorsFullyClosed, 2));
        Assert.Null(s.Elevators[0].ShownDirection);
    }

    [Fact]
    public void Utilization_is_the_share_of_time_not_idle_including_the_current_stretch()
    {
        var s = Fold(Started(), new StateChanged(S(10), 0, ElevatorState.Idle, ElevatorState.MovingUp, Trigger.StartMoveUp, 1),
            new StateChanged(S(30), 0, ElevatorState.MovingUp, ElevatorState.Idle, Trigger.DoorsFullyClosed, 5));
        s.AdvanceTo(S(40));
        Assert.Equal(0.5, s.Elevators[0].Utilization(s.Now), 6);
        s.Apply(new StateChanged(S(40), 0, ElevatorState.Idle, ElevatorState.MovingDown, Trigger.StartMoveDown, 5));
        s.AdvanceTo(S(60));
        Assert.Equal(40.0 / 60, s.Elevators[0].Utilization(s.Now), 6);
        Assert.Equal(0, s.Elevators[1].Utilization(s.Now));
    }

    [Fact]
    public void Modes_follow_the_v3_events_and_ops_time_is_accounted()
    {
        var s = Fold(Started(),
            new MaintenanceRequested(S(10), 1), new MaintenanceEntered(S(15), 1, 4));
        Assert.Equal(ElevatorMode.Maintenance, s.Elevators[1].Mode);
        s.Apply(new MaintenanceExited(S(75), 1));
        Assert.Equal(ElevatorMode.Normal, s.Elevators[1].Mode);
        Assert.Equal(60_000, s.Elevators[1].ModeMs(ElevatorMode.Maintenance, s.Now));
        Assert.Equal(5_000, s.Elevators[1].ModeMs(ElevatorMode.Draining, s.Now));

        s.Apply(new EmergencyStopped(S(100), 0, 3));
        Assert.Equal(ElevatorMode.Emergency, s.Elevators[0].Mode);
        s.AdvanceTo(S(110));
        Assert.Equal(10_000, s.Elevators[0].ModeMs(ElevatorMode.Emergency, s.Now)); // ongoing stretch counts
        s.Apply(new EmergencyCleared(S(130), 0));
        s.Apply(new FaultDetected(S(200), 0, 3, 30_000));
        Assert.Equal(ElevatorMode.Fault, s.Elevators[0].Mode);
        s.Apply(new FaultRecovered(S(245), 0));
        Assert.Equal(ElevatorMode.Normal, s.Elevators[0].Mode);

        Assert.Equal((1, 1), (s.Elevators[0].EmergencyStops, s.Elevators[0].Faults));
        Assert.Equal((30_000, 45_000), (s.Elevators[0].ModeMs(ElevatorMode.Emergency, s.Now), s.Elevators[0].ModeMs(ElevatorMode.Fault, s.Now)));
    }

    [Fact]
    public void Emergency_during_draining_returns_to_draining_when_cleared()
    {
        var s = Fold(Started(), new MaintenanceRequested(S(10), 0), new EmergencyStopped(S(12), 0, 2), new EmergencyCleared(S(20), 0));
        Assert.Equal(ElevatorMode.Draining, s.Elevators[0].Mode);
    }

    [Fact]
    public void Reassignments_count_and_ops_hand_backs_carry_a_wait_penalty()
    {
        var s = Fold(Started(),
            new PassengerArrived(S(1), 5, 2, 9),
            new AssignmentAccepted(S(1), 5, 0),
            new AssignmentReleased(S(10), 5, 0, AssignmentReason.Fault),
            new AssignmentAccepted(S(14), 5, 1),
            new AssignmentRejected(S(15), 5, 1, AssignmentReason.Stale),
            new AssignmentAccepted(S(16), 5, 0),
            new FaultCommandApplied(S(9), 0, FaultKind.Stuck, 60_000));
        Assert.Equal((2, 1, 4_000L, 1), (s.Reassignments, s.OpsReassignments, s.OpsPenaltyMs, s.CommandsApplied));
    }

    [Fact]
    public void Tail_keeps_the_last_items_and_skips_floor_noise_and_proposals_already_explained()
    {
        var s = Fold(Started(),
            new FloorReached(S(1), 0, 2),
            new DispatchEvaluated(S(2), 9, [new CandidateCost(0, 3.5), new CandidateCost(1, null)], 0),
            new AssignmentProposed(S(2), 9, 0, 3.5, null, false),
            new AssignmentProposed(S(3), 10, 1, 1, null, false));
        s.Apply(new UnknownEvent(S(4), "Teleported", []));
        Assert.Equal(["SimulationStarted", "DispatchEvaluated", "AssignmentProposed", "Teleported"],
            s.Tail.Select(t => t.Known?.GetType().Name ?? t.Unknown!.Type));
        Assert.Equal(10, ((AssignmentProposed)s.Tail[2].Known!).RequestId);

        for (var i = 0; i < BuildingState.TailCapacity + 10; i++) s.Apply(new DuplicateSubmission(S(5), i));
        Assert.Equal(BuildingState.TailCapacity, s.Tail.Count);
        Assert.Equal(BuildingState.TailCapacity + 9, ((DuplicateSubmission)s.Tail[^1].Known!).RequestId);
    }

    [Fact]
    public void Unknown_events_and_out_of_range_ids_never_throw()
    {
        var s = Fold(Started(elevators: 1), new FloorReached(S(1), 5, 3), new MaintenanceRequested(S(1), -1));
        s.Apply(new UnknownEvent(S(2), "X", []));
        Assert.Equal(S(2), s.Now);
        Assert.Equal(6, s.Elevators.Count); // grown to show what the log says
    }

    public static TheoryData<string, string> Runs => new()
    {
        { "lunch.yaml", "cost" }, { "up-peak.yaml", "nearest" }, { "down-peak.yaml", "roundrobin" }, { "v1-basic.yaml", "cost" }, { "demo.yaml", "cost" },
        { "fleet.yaml", "cost" }, // typed fleet with freight loads (v3.1: load units, per-elevator capacity and type)
    };

    /// <summary>
    /// The fold is proved against the engine's own read-only views: at sampled times, every elevator's floor, state,
    /// shown direction, riders and mode, and the waiting counts, match <see cref="SimEngine.Elevators"/> and
    /// <see cref="SimEngine.Waiting"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runs))]
    public void Fold_matches_the_engine_snapshots_at_sampled_times(string scenario, string dispatch)
    {
        var engine = SimEngine.Create(Repo.Load(scenario) with { Dispatch = dispatch }, options: new SimEngineOptions(EmitDispatchEvaluations: true));
        var state = new BuildingState();
        var folded = 0;
        var samples = 0;
        while (!engine.Finished)
        {
            engine.AdvanceTo(engine.Now.AddMs(1_300 + (samples * 7_919 % 5_000))); // irregular sample times
            for (; folded < engine.Log.Count; folded++) state.Apply(engine.Log[folded]);
            state.AdvanceTo(engine.Now);
            samples++;

            var expected = engine.Elevators.Select(e => (e.ElevatorId, e.CurrentFloor, e.State, ShownFrom(e), e.Load, e.Mode, e.Capacity, e.TypeName));
            var actual = state.Elevators.Select(e => (e.Id, e.Floor, e.State, e.ShownDirection, e.Riders, e.Mode, e.Capacity ?? state.Capacity, e.TypeName));
            Assert.Equal(expected, actual);
            Assert.Equal(engine.Waiting.OrderBy(kv => kv.Key), state.Waiting.OrderBy(kv => kv.Key));
            Assert.Equal(engine.Delivered, state.Delivered);
        }
        Assert.True(samples > 10);
    }

    /// <summary>The documented display rule applied to a snapshot: travel direction, else the lantern while doors are open or closing.</summary>
    private static Direction? ShownFrom(ElevatorSnapshot s) => s.State switch
    {
        ElevatorState.MovingUp => Direction.Up,
        ElevatorState.MovingDown => Direction.Down,
        ElevatorState.DoorOpen or ElevatorState.DoorClosing => s.CommittedDirection,
        _ => null,
    };
}
