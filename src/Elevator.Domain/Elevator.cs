using Elevator.Domain.Events;

namespace Elevator.Domain;

/// <summary>
/// One elevator: a pure, single-threaded state machine (ADR-002, ADR-003).
///
/// It never reads a clock. Time only advances through <see cref="Tick"/>, which runs every timed phase whose
/// deadline has passed (one floor of travel, doors opening, dwell, doors closing). Because deadlines are
/// processed one at a time at their exact instant, the outcome does not depend on how often Tick is called.
///
/// Mechanics (fixed, whatever the policy):
/// <list type="bullet">
/// <item>On reaching a floor it stops if a car call is registered there or the floor is the policy's goal.
/// It never carries a rider past their floor.</item>
/// <item>On stopping it lights a direction lantern. Only the hall call in that direction is answered, so only
/// riders going that way board.</item>
/// <item>A matching hall call at the floor where it stands is answered at once (doors open), reopens the doors
/// (doors closing) or opens them (idle).</item>
/// </list>
/// The policy only answers "which floor next?" (<see cref="ISchedulingPolicy.NextGoal"/>).
/// </summary>
public sealed class Elevator
{
    private readonly ElevatorTiming _timing;
    private readonly ISchedulingPolicy _policy;
    private readonly SortedSet<int> _carCalls = [];
    private readonly List<HallCall> _hallCalls = []; // registration order = FIFO order
    private readonly List<DomainEvent> _outbox = [];

    private SimTime? _deadline;       // end of the current timed phase; null in Idle/Maintenance
    private SimTime _closingStartedAt; // used to compute how long a reopen takes
    private bool _levelWithFloor;     // true while stopped, or at the instant a moving elevator reaches a floor

    public Elevator(int id, int floors, ElevatorTiming timing, ISchedulingPolicy policy, int startFloor = Floors.Lowest)
    {
        if (floors < 2) throw new ArgumentOutOfRangeException(nameof(floors), "need at least two floors");
        if (Floors.Check(startFloor, floors) is { } error) throw new ArgumentOutOfRangeException(nameof(startFloor), error);
        if (timing.Validate() is [var first, ..]) throw new ArgumentException(first, nameof(timing));

        Id = id;
        FloorCount = floors;
        _timing = timing;
        _policy = policy;
        CurrentFloor = startFloor;
        _levelWithFloor = true;
    }

    public int Id { get; }

    public int FloorCount { get; }

    /// <summary>The floor the elevator is at, or the last floor it passed while moving.</summary>
    public int CurrentFloor { get; private set; }

    public ElevatorState State { get; private set; } = ElevatorState.Idle;

    /// <summary>Direction of travel while moving; the lit lantern while stopped; null when there is no work.</summary>
    public Direction? CommittedDirection { get; private set; }

    public SimTime Now { get; private set; } = SimTime.Zero;

    /// <summary>When the next timed phase ends, if any. Lets a discrete-event runner jump straight to it.</summary>
    public SimTime? NextDeadline => _deadline;

    public IReadOnlyCollection<int> CarCalls => _carCalls;

    public IReadOnlyList<HallCall> HallCalls => _hallCalls;

    /// <summary>Spec's <c>targetFloors</c>: floors with a pending stop, car calls (ascending) then hall calls (oldest first).</summary>
    public IReadOnlyList<int> TargetFloors => _carCalls.Concat(_hallCalls.Select(c => c.Floor)).Distinct().ToList();

    public ElevatorSnapshot Snapshot() =>
        new(Id, CurrentFloor, State, CommittedDirection, [.. _carCalls], [.. _hallCalls], FloorCount);

    /// <summary>Returns and clears the events produced since the last call.</summary>
    public IReadOnlyList<DomainEvent> DrainEvents()
    {
        var events = _outbox.ToArray();
        _outbox.Clear();
        return events;
    }

    // ---- Time -----------------------------------------------------------------------------------------

    /// <summary>Advances sim time to <paramref name="now"/>, running every phase that ends on or before it.</summary>
    public void Tick(SimTime now)
    {
        if (now < Now) throw new ArgumentOutOfRangeException(nameof(now), $"time went backwards: {now} < {Now}");

        while (_deadline is { } deadline && deadline <= now)
        {
            Now = deadline;
            _deadline = null;
            OnPhaseEnded();
        }
        Now = now;
    }

    private void OnPhaseEnded()
    {
        switch (State)
        {
            case ElevatorState.MovingUp or ElevatorState.MovingDown:
                OnFloorReached();
                break;
            case ElevatorState.DoorOpening:
                Fire(Trigger.DoorsFullyOpen);
                // Stopped without work (lantern off) but someone pressed a button here meanwhile: serve them.
                CommittedDirection ??= _hallCalls.FirstOrDefault(c => c.Floor == CurrentFloor)?.Direction;
                _outbox.Add(new DoorsOpened(Now, Id, CurrentFloor, CommittedDirection));
                AnswerHallCallHere();
                _deadline = Now.AddMs(_timing.DoorDwellMs);
                break;
            case ElevatorState.DoorOpen:
                CloseDoor();
                break;
            case ElevatorState.DoorClosing:
                Fire(Trigger.DoorsFullyClosed);
                DecideFromIdle();
                break;
        }
    }

    private void OnFloorReached()
    {
        var direction = State == ElevatorState.MovingUp ? Direction.Up : Direction.Down;
        CurrentFloor += direction == Direction.Up ? 1 : -1;
        _outbox.Add(new FloorReached(Now, Id, CurrentFloor));

        _levelWithFloor = true;
        if (ShouldStopHere(direction))
        {
            OpenDoor();
            return;
        }
        _levelWithFloor = false;
        _deadline = Now.AddMs(_timing.FloorTravelMs);
    }

    /// <summary>
    /// Stop for a car call here (always), at the policy's goal, or when the goal is no longer ahead.
    /// The last case only arises from a car call pressed behind the elevator while it moves: a real elevator
    /// cannot reverse in mid-flight, so it stops at the next floor and decides again from there.
    /// </summary>
    private bool ShouldStopHere(Direction direction)
    {
        if (_carCalls.Contains(CurrentFloor)) return true;
        if (CurrentFloor == Floors.Lowest || CurrentFloor == FloorCount) return true;
        var goal = _policy.NextGoal(Snapshot());
        return goal is null || goal == CurrentFloor || !direction.IsAhead(CurrentFloor, goal.Value);
    }

    // ---- Spec methods: moveUp(), moveDown(), openDoor(), closeDoor() -----------------------------------
    // Tick and call registration drive these; they are public so the state machine can be exercised directly.
    // Each one goes through the transition table, so calling one in the wrong state throws.

    public void MoveUp() => StartMoving(Trigger.StartMoveUp, Direction.Up);

    public void MoveDown() => StartMoving(Trigger.StartMoveDown, Direction.Down);

    private void StartMoving(Trigger trigger, Direction direction)
    {
        if (State == ElevatorState.Idle && (direction == Direction.Up ? CurrentFloor == FloorCount : CurrentFloor == Floors.Lowest))
            throw new InvalidOperationException($"cannot move {direction} from floor {CurrentFloor}");

        Fire(trigger);
        CommittedDirection = direction;
        _levelWithFloor = false;
        _deadline = Now.AddMs(_timing.FloorTravelMs);
    }

    /// <summary>
    /// Opens the doors at the current floor. While doors are closing this reverses them, otherwise it is a stop
    /// (from Idle, or from a moving state at the instant the elevator is level with a floor).
    /// </summary>
    public void OpenDoor()
    {
        if (State == ElevatorState.DoorClosing)
        {
            Fire(Trigger.Reopen);
            // Doors reverse from where they are: having closed for x ms, they need x·(opening/closing) ms to be open again.
            var closedFor = Now.Ms - _closingStartedAt.Ms;
            _deadline = Now.AddMs(closedFor * _timing.DoorOpeningMs / _timing.DoorClosingMs);
            return;
        }

        if (!_levelWithFloor) throw new InvalidOperationException("cannot open the doors between floors");

        _carCalls.Remove(CurrentFloor);
        var lantern = LanternForStop();
        Fire(Trigger.ArriveAndOpen);
        CommittedDirection = lantern;
        _deadline = Now.AddMs(_timing.DoorOpeningMs);
    }

    public void CloseDoor()
    {
        Fire(Trigger.StartClosing);
        _closingStartedAt = Now;
        _deadline = Now.AddMs(_timing.DoorClosingMs);
    }

    public void EnterMaintenance() => Fire(Trigger.EnterMaintenance);

    public void ExitMaintenance()
    {
        Fire(Trigger.ExitMaintenance);
        DecideFromIdle();
    }

    // ---- Calls ----------------------------------------------------------------------------------------

    /// <summary>Registers a hall call. Input must already be valid (see <see cref="ElevatorController"/>).</summary>
    public void AddHallCall(int floor, Direction direction)
    {
        if (Floors.CheckHallButton(floor, direction, FloorCount) is { } error) throw new ArgumentOutOfRangeException(nameof(floor), error);
        if (_hallCalls.Any(c => c.Floor == floor && c.Direction == direction)) return; // button already lit

        _hallCalls.Add(new HallCall(floor, direction, Now));
        _outbox.Add(new HallCallRegistered(Now, floor, direction));

        if (State == ElevatorState.Idle)
        {
            DecideFromIdle();
            return;
        }

        var standingHereWithDoorsInCycle =
            floor == CurrentFloor && State is ElevatorState.DoorOpening or ElevatorState.DoorOpen or ElevatorState.DoorClosing;
        if (!standingHereWithDoorsInCycle) return;

        CommittedDirection ??= direction; // stopped with no work: adopt the caller's direction
        if (CommittedDirection != direction) return; // served later, when the elevator comes back for it

        switch (State)
        {
            case ElevatorState.DoorOpen:
                AnswerHallCallHere();
                _deadline = Now.AddMs(_timing.DoorDwellMs); // give the newcomer a full dwell
                break;
            case ElevatorState.DoorClosing:
                OpenDoor(); // reopen; answered when fully open
                break;
            // DoorOpening: answered when the doors are fully open.
        }
    }

    /// <summary>Spec's <c>addRequest</c>: a car call (a destination pressed inside the elevator).</summary>
    public void AddRequest(int floor)
    {
        if (Floors.Check(floor, FloorCount) is { } error) throw new ArgumentOutOfRangeException(nameof(floor), error);
        if (floor == CurrentFloor && _levelWithFloor) return; // already here
        if (!_carCalls.Add(floor)) return;

        _outbox.Add(new CarCallRegistered(Now, Id, floor));
        if (State == ElevatorState.Idle) DecideFromIdle();
    }

    // ---- Decisions ------------------------------------------------------------------------------------

    private void DecideFromIdle()
    {
        if (State != ElevatorState.Idle) return;

        var goal = _policy.NextGoal(Snapshot());
        if (goal is null) CommittedDirection = null;
        else if (goal == CurrentFloor) OpenDoor();
        else if (goal > CurrentFloor) MoveUp();
        else MoveDown();
    }

    /// <summary>
    /// The lantern shown at a stop: towards the next goal; if the goal is this floor, the direction of the oldest
    /// hall call here (the one we came for); null when there is nothing left to do.
    /// </summary>
    private Direction? LanternForStop()
    {
        var goal = _policy.NextGoal(Snapshot());
        if (goal is null) return null;
        if (goal != CurrentFloor) return Directions.Between(CurrentFloor, goal.Value);
        return _hallCalls.FirstOrDefault(c => c.Floor == CurrentFloor)?.Direction;
    }

    private void AnswerHallCallHere()
    {
        var index = _hallCalls.FindIndex(c => c.Floor == CurrentFloor && c.Direction == CommittedDirection);
        if (index < 0) return;

        var call = _hallCalls[index];
        _hallCalls.RemoveAt(index);
        _outbox.Add(new HallCallAnswered(Now, Id, call.Floor, call.Direction));
    }

    private void Fire(Trigger trigger)
    {
        var from = State;
        State = TransitionTable.Next(from, trigger);
        if (State is ElevatorState.Idle or ElevatorState.DoorOpening) _levelWithFloor = true;
        _outbox.Add(new StateChanged(Now, Id, from, State, trigger, CurrentFloor));
    }
}
