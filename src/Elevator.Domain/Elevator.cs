using System.Collections.Immutable;
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
/// <item>On reaching a floor it stops if a drop-off is registered there or the floor is the policy's target.
/// It never carries a rider past their floor.</item>
/// <item>On stopping it lights a direction lantern. Only the pickup in that direction is answered, so only
/// riders going that way board.</item>
/// <item>A matching pickup at the floor where it stands is answered at once (doors open), reopens the doors
/// (doors closing) or opens them (idle).</item>
/// <item>Full-load bypass: at or above the bypass threshold it only serves its riders. In NextTrip mode (default,
/// ADR-010) it keeps its pickups and comes back for them once riders get off; in Defer mode it hands them back
/// (<see cref="AssignmentReleased"/>).</item>
/// </list>
/// The policy only answers "where next, and which lantern?" (<see cref="ISchedulingPolicy.Decide"/>).
/// </summary>
public sealed class Elevator
{
    private readonly ElevatorTiming _timing;
    private readonly ISchedulingPolicy _policy;
    // Persistent immutable collections: a snapshot shares them instead of copying, so publishing a snapshot on every
    // change costs O(1) however many stops are queued (this mattered for perf under large backlogs, ADR-005).
    private ImmutableSortedSet<int> _carCalls = [];
    private ImmutableList<HallCall> _hallCalls = []; // registration order = FIFO order
    private ImmutableList<Request> _riders = [];
    private DestinationTotals _riderTotals = DestinationTotals.Empty;
    private readonly List<DomainEvent> _outbox = [];

    private SimTime? _deadline;       // end of the current timed phase; null in Idle/Maintenance
    private SimTime _closingStartedAt; // used to compute how long a reopen takes
    private bool _levelWithFloor;     // true while stopped, or at the instant a moving elevator reaches a floor

    public Elevator(int id, int floors, ElevatorTiming timing, ISchedulingPolicy policy, int startFloor = Floors.Lowest,
        int capacity = int.MaxValue, double bypassThreshold = 1.0, CapacityMode capacityMode = CapacityMode.NextTrip)
    {
        if (floors < 2) throw new ArgumentOutOfRangeException(nameof(floors), "need at least two floors");
        if (Floors.Check(startFloor, floors) is { } error) throw new ArgumentOutOfRangeException(nameof(startFloor), error);
        if (timing.Validate() is [var first, ..]) throw new ArgumentException(first, nameof(timing));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (bypassThreshold is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(bypassThreshold), "must be in (0, 1]");

        Id = id;
        FloorCount = floors;
        Capacity = capacity;
        BypassThreshold = bypassThreshold;
        CapacityMode = capacityMode;
        _timing = timing;
        _policy = policy;
        CurrentFloor = startFloor;
        _levelWithFloor = true;
    }

    /// <summary>
    /// Rebuilds an elevator in exactly the state captured by <paramref name="s"/>, including a phase in progress.
    /// Used to project an elevator's future route with its own mechanics and policy (RouteProjection).
    /// </summary>
    public static Elevator FromSnapshot(ElevatorSnapshot s, ElevatorTiming timing, ISchedulingPolicy policy)
    {
        var e = new Elevator(s.ElevatorId, s.Floors, timing, policy, s.CurrentFloor, s.Capacity, s.BypassThreshold, s.CapacityMode)
        {
            State = s.State,
            CommittedDirection = s.CommittedDirection,
            Now = s.At,
            Version = s.Version,
            _deadline = s.PhaseEndsAt,
            _closingStartedAt = s.ClosingStartedAt,
            _levelWithFloor = !s.IsMoving,
        };
        e._carCalls = s.CarCalls as ImmutableSortedSet<int> ?? [.. s.CarCalls];
        e._hallCalls = s.HallCalls as ImmutableList<HallCall> ?? [.. s.HallCalls];
        e._riders = s.Riders as ImmutableList<Request> ?? [.. s.Riders];
        e._riderTotals = s.RiderTotals;
        return e;
    }

    public int Id { get; }

    public int FloorCount { get; }

    public int Capacity { get; }

    public double BypassThreshold { get; }

    public CapacityMode CapacityMode { get; }

    /// <summary>NextTrip mode and full: pickups are bypassed (not served, not shown to the policy) until there is room.</summary>
    private bool Bypassing => CapacityMode == CapacityMode.NextTrip && IsFull;

    /// <summary>The floor the elevator is at, or the last floor it passed while moving.</summary>
    public int CurrentFloor { get; private set; }

    public ElevatorState State { get; private set; } = ElevatorState.Idle;

    /// <summary>Direction of travel while moving; the lit lantern while stopped; null when there is no work.</summary>
    public Direction? CommittedDirection { get; private set; }

    public SimTime Now { get; private set; } = SimTime.Zero;

    /// <summary>Increments on every observable change.</summary>
    public long Version { get; private set; }

    /// <summary>When the next timed phase ends, if any. Lets a discrete-event runner jump straight to it.</summary>
    public SimTime? NextDeadline => _deadline;

    public IReadOnlyCollection<int> CarCalls => _carCalls;

    public IReadOnlyList<HallCall> HallCalls => _hallCalls;

    public IReadOnlyList<Request> Riders => _riders;

    public int Load => _riderTotals.TotalLoad;

    public bool IsFull => Load >= BypassThreshold * Capacity;

    /// <summary>Spec's <c>targetFloors</c>: floors with a pending stop, car calls (ascending) then hall calls (oldest first).</summary>
    public IReadOnlyList<int> TargetFloors => _carCalls.Concat(_hallCalls.Select(c => c.Floor)).Distinct().ToList();

    public ElevatorSnapshot Snapshot() =>
        new(Id, CurrentFloor, State, CommittedDirection, _carCalls, _hallCalls, FloorCount, _riders,
            Capacity, BypassThreshold, Now, _deadline, _closingStartedAt, Version) { RiderTotals = _riderTotals, CapacityMode = CapacityMode };

    /// <summary>What the scheduling policy sees: while bypassing, only the drop-offs.</summary>
    private ElevatorSnapshot PolicyView() => Bypassing ? Snapshot() with { HallCalls = [] } : Snapshot();

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
                Emit(new DoorsOpened(Now, Id, CurrentFloor, CommittedDirection));
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
        Emit(new FloorReached(Now, Id, CurrentFloor));

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
    /// Stop for a drop-off here (always), at the policy's target, or when the target is no longer ahead.
    /// The last case only arises from a car call pressed behind the elevator while it moves: a real elevator
    /// cannot reverse in mid-flight, so it stops at the next floor and decides again from there.
    /// </summary>
    private bool ShouldStopHere(Direction direction)
    {
        if (_carCalls.Contains(CurrentFloor)) return true;
        if (CurrentFloor == Floors.Lowest || CurrentFloor == FloorCount) return true;
        var target = _policy.Decide(PolicyView()).Target;
        return target is null || target == CurrentFloor || !direction.IsAhead(CurrentFloor, target.Value);
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

        _carCalls = _carCalls.Remove(CurrentFloor);
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

    /// <summary>If idle, decide again whether there is work. Needed after <see cref="FromSnapshot"/> of a hand-built snapshot.</summary>
    public void Resume() => DecideFromIdle();

    public void ExitMaintenance()
    {
        Fire(Trigger.ExitMaintenance);
        DecideFromIdle();
    }

    // ---- Calls ----------------------------------------------------------------------------------------

    /// <summary>Registers a hall call (v1 mode: a lit button, no request attached). Input must already be valid.</summary>
    public void AddHallCall(int floor, Direction direction) => AddPickup(floor, direction, request: null);

    /// <summary>
    /// Destination dispatch: the dispatcher assigned <paramref name="request"/> to this elevator. It joins the pickup
    /// stop at its floor and direction, creating the stop if needed. Accept/reject is decided before this call.
    /// </summary>
    public void AssignPickup(Request request) => AddPickup(request.PickupFloor, request.Direction, request);

    private void AddPickup(int floor, Direction direction, Request? request)
    {
        if (Floors.CheckHallButton(floor, direction, FloorCount) is { } error) throw new ArgumentOutOfRangeException(nameof(floor), error);

        var index = _hallCalls.FindIndex(c => c.Floor == floor && c.Direction == direction);
        if (index >= 0)
        {
            // Already a stop here in this direction (lit button / dedup): the request just joins it.
            if (request is not null)
            {
                var call = _hallCalls[index];
                var requests = call.Requests as ImmutableList<Request> ?? [.. call.Requests];
                _hallCalls = _hallCalls.SetItem(index, call with { Requests = requests.Add(request), Totals = call.Totals.Add(request) });
                Version++;
            }
            return;
        }

        _hallCalls = _hallCalls.Add(request is null
            ? new HallCall(floor, direction, Now)
            : new HallCall(floor, direction, Now, ImmutableList.Create(request)) { Totals = DestinationTotals.Empty.Add(request) });
        Version++;
        Emit(new HallCallRegistered(Now, floor, direction, Id));

        if (State == ElevatorState.Idle)
        {
            DecideFromIdle();
            return;
        }

        var standingHereWithDoorsInCycle =
            floor == CurrentFloor && State is ElevatorState.DoorOpening or ElevatorState.DoorOpen or ElevatorState.DoorClosing;
        if (!standingHereWithDoorsInCycle) return;

        if (Bypassing) return; // full: served on a later trip
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
        if (_carCalls.Contains(floor)) return;
        _carCalls = _carCalls.Add(floor);

        Version++;
        Emit(new CarCallRegistered(Now, Id, floor));
        if (State == ElevatorState.Idle) DecideFromIdle();
    }

    /// <summary>
    /// Load sensor: <paramref name="rider"/> stepped in, and their destination becomes a drop-off. If that brings
    /// the load to the bypass threshold, every pending pickup is handed back to the dispatcher.
    /// </summary>
    public void Board(Request rider)
    {
        _riders = _riders.Add(rider);
        _riderTotals = _riderTotals.Add(rider);
        Version++;
        AddRequest(rider.DestinationFloor);
        if (CapacityMode == CapacityMode.Defer && IsFull) ReleaseAllPickups(AssignmentReason.BypassedFull);
    }

    /// <summary>Load sensor: the rider with <paramref name="requestId"/> stepped out.</summary>
    public void Alight(int requestId)
    {
        var index = _riders.FindIndex(r => r.Id == requestId);
        if (index < 0) return;
        _riderTotals = _riderTotals.Remove(_riders[index]);
        _riders = _riders.RemoveAt(index);
        Version++;

        // Riders getting off can make room while the doors are open: a bypassed pickup here is answered now.
        if (CapacityMode == CapacityMode.NextTrip && State == ElevatorState.DoorOpen && !IsFull && AnswerHallCallHere())
            _deadline = Now.AddMs(_timing.DoorDwellMs);
        // Idle and no longer full: the bypassed pickups are visible to the policy again.
        if (State == ElevatorState.Idle) DecideFromIdle();
    }

    /// <summary>Removes a not-yet-picked-up request (rebalancing). False if it is no longer pending here.</summary>
    public bool Unassign(int requestId)
    {
        var index = _hallCalls.FindIndex(c => c.Requests.Any(r => r.Id == requestId));
        if (index < 0) return false;

        var call = _hallCalls[index];
        var removed = call.Requests.First(r => r.Id == requestId);
        var remaining = call.Requests.Where(r => r.Id != requestId).ToImmutableList();
        _hallCalls = remaining.Count == 0
            ? _hallCalls.RemoveAt(index)
            : _hallCalls.SetItem(index, call with { Requests = remaining, Totals = call.Totals.Remove(removed) });
        Version++;
        return true;
    }

    private void ReleaseAllPickups(AssignmentReason reason)
    {
        foreach (var call in _hallCalls)
        foreach (var request in call.Requests)
            Emit(new AssignmentReleased(Now, request.Id, Id, reason));
        _hallCalls = _hallCalls.RemoveAll(c => c.Requests.Count > 0);
        Version++;
    }

    // ---- Decisions ------------------------------------------------------------------------------------

    private void DecideFromIdle()
    {
        if (State != ElevatorState.Idle) return;

        var target = _policy.Decide(PolicyView()).Target;
        if (target is null) CommittedDirection = null;
        else if (target == CurrentFloor) OpenDoor();
        else if (target > CurrentFloor) MoveUp();
        else MoveDown();
    }

    /// <summary>
    /// The lantern shown at a stop: towards the next target; if the target is this floor, the direction the policy
    /// says to serve (default: the oldest pickup here, the one we came for); null when there is nothing left to do.
    /// </summary>
    private Direction? LanternForStop()
    {
        var decision = _policy.Decide(PolicyView());
        if (decision.Target is not { } target) return null;
        if (target != CurrentFloor) return Directions.Between(CurrentFloor, target);
        return decision.ServeDirection ?? _hallCalls.FirstOrDefault(c => c.Floor == CurrentFloor)?.Direction;
    }

    private bool AnswerHallCallHere()
    {
        if (Bypassing) return false; // full: pickups wait for the next trip
        var index = _hallCalls.FindIndex(c => c.Floor == CurrentFloor && c.Direction == CommittedDirection);
        if (index < 0) return false;

        var call = _hallCalls[index];
        _hallCalls = _hallCalls.RemoveAt(index);
        Version++;
        Emit(new HallCallAnswered(Now, Id, call.Floor, call.Direction));
        return true;
    }

    private void Fire(Trigger trigger)
    {
        var from = State;
        State = TransitionTable.Next(from, trigger);
        if (State is ElevatorState.Idle or ElevatorState.DoorOpening) _levelWithFloor = true;
        Version++;
        Emit(new StateChanged(Now, Id, from, State, trigger, CurrentFloor));
    }

    private void Emit(DomainEvent e) => _outbox.Add(e);
}
