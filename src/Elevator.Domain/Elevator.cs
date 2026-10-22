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

    private SimTime? _deadline;       // end of the current timed phase; null in Idle/Maintenance, and while stuck
    private bool _maintenanceRequested; // draining, then parked (ADR-013)
    private bool _emergency;          // emergency stop in effect (ADR-013)
    private bool _faulted;            // declared faulty by the watchdog, until it moves again (ADR-013)
    private bool _stuck;              // the drive is stuck (fault injection, ADR-013): no phase completes
    private long? _frozenMs;          // while stuck: what is left of the current phase
    private SimTime _closingStartedAt; // used to compute how long a reopen takes
    private bool _levelWithFloor;     // true while stopped, or at the instant a moving elevator reaches a floor

    /// <param name="servedFloors">Floors this elevator serves (its type, ADR-012): it never stops at any other.</param>
    /// <param name="typeName">Its type's name (Local, Express, Freight...).</param>
    public Elevator(int id, int floors, ElevatorTiming timing, ISchedulingPolicy policy, int startFloor = Floors.Lowest,
        int capacity = int.MaxValue, double bypassThreshold = 1.0, CapacityMode capacityMode = CapacityMode.NextTrip,
        ulong servedFloors = FloorMask.All, string typeName = ElevatorType.StandardName)
    {
        if (floors < 2) throw new ArgumentOutOfRangeException(nameof(floors), "need at least two floors");
        if (Floors.Check(startFloor, floors) is { } error) throw new ArgumentOutOfRangeException(nameof(startFloor), error);
        if (!FloorMask.Contains(servedFloors, startFloor))
            throw new ArgumentOutOfRangeException(nameof(startFloor), $"floor {startFloor} is not served by this elevator");
        if (timing.Validate() is [var first, ..]) throw new ArgumentException(first, nameof(timing));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (bypassThreshold is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(bypassThreshold), "must be in (0, 1]");

        Id = id;
        FloorCount = floors;
        ServedFloors = servedFloors;
        TypeName = typeName;
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
        // Built at a served floor, then placed where the snapshot says: mid-route it may be passing an unserved one.
        var anyServed = Enumerable.Range(Floors.Lowest, s.Floors).First(f => FloorMask.Contains(s.ServedFloors, f));
        var e = new Elevator(s.ElevatorId, s.Floors, s.Timing ?? timing, policy, anyServed, s.Capacity, s.BypassThreshold, s.CapacityMode,
            s.ServedFloors, s.TypeName)
        {
            CurrentFloor = s.CurrentFloor,
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

    /// <summary>Floors this elevator serves (<see cref="FloorMask"/>). It passes the others without stopping.</summary>
    public ulong ServedFloors { get; }

    public string TypeName { get; }

    public ElevatorTiming Timing => _timing;

    public bool Serves(int floor) => FloorMask.Contains(ServedFloors, floor);

    public int Capacity { get; }

    public double BypassThreshold { get; }

    public CapacityMode CapacityMode { get; }

    /// <summary>NextTrip mode and full: pickups are bypassed (not served, not shown to the policy) until there is room.</summary>
    private bool Bypassing => CapacityMode == CapacityMode.NextTrip && IsFull;

    /// <summary>The floor the elevator is at, or the last floor it passed while moving.</summary>
    public int CurrentFloor { get; private set; }

    public ElevatorState State { get; private set; } = ElevatorState.Idle;

    /// <summary>
    /// Operating mode (ADR-013), alongside the spec's state: Emergency while an emergency stop is in effect (it wins
    /// over everything), Maintenance once parked, Fault from the watchdog's declaration until it moves again, Draining
    /// while maintenance is requested and riders are still on board. Only a Normal elevator takes new assignments.
    /// </summary>
    public ElevatorMode Mode =>
        _emergency ? ElevatorMode.Emergency
        : State == ElevatorState.Maintenance ? ElevatorMode.Maintenance
        : _faulted ? ElevatorMode.Fault
        : _maintenanceRequested ? ElevatorMode.Draining
        : ElevatorMode.Normal;

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
            Capacity, BypassThreshold, Now, _deadline, _closingStartedAt, Version)
        {
            RiderTotals = _riderTotals,
            CapacityMode = CapacityMode,
            Timing = _timing,
            TypeName = TypeName,
            ServedFloors = ServedFloors,
            Mode = Mode,
        };

    /// <summary>
    /// What the scheduling policy sees: while bypassing, only the drop-offs. Otherwise every pickup, except those where
    /// someone would not fit in the room left (a heavy load, ADR-012): they are bypassed the same way until riders get
    /// off. Passengers board in strict arrival order, so when every load at a stop fits, the first in line does and a stop
    /// there always boards someone; without this, a policy that serves "a pickup right here" first would reopen for a
    /// load that never fits, forever. Once empty, every load fits (the capacity access policy sees to it). With one-unit
    /// loads this never hides anything: no room at all means full, which bypasses every pickup already.
    /// </summary>
    private ElevatorSnapshot PolicyView()
    {
        if (Bypassing) return Snapshot() with { HallCalls = [] };
        var room = (long)Capacity - Load;
        if (!_hallCalls.Any(c => c.MaxLoad > room)) return Snapshot();
        return Snapshot() with { HallCalls = [.. _hallCalls.Where(c => c.MaxLoad <= room)] };
    }

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
            if (_faulted) Recover(); // a timed phase completed: it moves again
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
                StartPhase(_timing.DoorDwellMs);
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
        if (_emergency)
        {
            // The next safe point: stopped level with a floor, doors closed. Riders stay inside until it is cleared.
            Fire(Trigger.EmergencyHalt);
            CommittedDirection = null;
            return;
        }
        if (ShouldStopHere(direction))
        {
            OpenDoor();
            return;
        }
        _levelWithFloor = false;
        StartPhase(_timing.FloorTravelMs);
    }

    /// <summary>
    /// Stop for a drop-off here (always), at the policy's target, or when the target is no longer ahead.
    /// The last case only arises from a car call pressed behind the elevator while it moves: a real elevator
    /// cannot reverse in mid-flight, so it stops at the next floor and decides again from there.
    /// A floor it does not serve (an express passing the local floors) is never a stop: it carries on to the next
    /// served floor and decides there. Every stop it can have is a served floor, so one always lies ahead.
    /// </summary>
    private bool ShouldStopHere(Direction direction)
    {
        if (_carCalls.Contains(CurrentFloor)) return true;
        if (CurrentFloor == Floors.Lowest || CurrentFloor == FloorCount) return true;
        if (!Serves(CurrentFloor)) return false;
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
        StartPhase(_timing.FloorTravelMs);
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
            var closedFor = _stuck ? _timing.DoorClosingMs - (_frozenMs ?? 0) : Now.Ms - _closingStartedAt.Ms;
            StartPhase(closedFor * _timing.DoorOpeningMs / _timing.DoorClosingMs);
            return;
        }

        if (!_levelWithFloor) throw new InvalidOperationException("cannot open the doors between floors");

        _carCalls = _carCalls.Remove(CurrentFloor);
        var lantern = LanternForStop();
        Fire(Trigger.ArriveAndOpen);
        CommittedDirection = lantern;
        StartPhase(_timing.DoorOpeningMs);
    }

    public void CloseDoor()
    {
        Fire(Trigger.StartClosing);
        _closingStartedAt = Now;
        StartPhase(_timing.DoorClosingMs);
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
        if (!Serves(floor)) throw new ArgumentOutOfRangeException(nameof(floor), NotServed(floor));

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

        if (Bypassing || _emergency) return; // full: served on a later trip; emergency: doors stay closed
        // A load that cannot fit now (freight, ADR-012) is not answered at the open door: it would be left behind and,
        // re-dispatched to this same elevator, answered again at the same instant, forever. It waits for a later trip.
        // With one-unit loads this never applies: not fitting means full, which bypasses above.
        if (request is not null && Load + request.Load > Capacity) return;
        CommittedDirection ??= direction; // stopped with no work: adopt the caller's direction
        if (CommittedDirection != direction) return; // served later, when the elevator comes back for it

        switch (State)
        {
            case ElevatorState.DoorOpen:
                AnswerHallCallHere();
                StartPhase(_timing.DoorDwellMs); // give the newcomer a full dwell
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
        if (!Serves(floor)) throw new ArgumentOutOfRangeException(nameof(floor), NotServed(floor)); // access policies prevent this
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
            StartPhase(_timing.DoorDwellMs);
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

    /// <summary>
    /// Hands every pending pickup back, oldest call first (ADR-015): re-dispatch keeps each passenger's place by call
    /// time (then id), not by where their stop sat in this elevator's list.
    /// </summary>
    private void ReleaseAllPickups(AssignmentReason reason)
    {
        foreach (var request in _hallCalls.SelectMany(c => c.Requests).OrderBy(r => r.Timestamp.Ms).ThenBy(r => r.Id))
            Emit(new AssignmentReleased(Now, request.Id, Id, reason));
        _hallCalls = _hallCalls.RemoveAll(c => c.Requests.Count > 0);
        Version++;
    }

    // ---- Decisions ------------------------------------------------------------------------------------

    private void DecideFromIdle()
    {
        if (State != ElevatorState.Idle) return;
        if (_emergency)
        {
            CommittedDirection = null; // stays put, doors closed, until cleared
            return;
        }

        var target = _policy.Decide(PolicyView()).Target;
        if (target is null)
        {
            CommittedDirection = null;
            ParkIfDrained();
        }
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

    /// <summary>Starts a timed phase of <paramref name="ms"/>; while stuck it is frozen with all its time left.</summary>
    private void StartPhase(long ms)
    {
        if (_stuck) _frozenMs = ms;
        else _deadline = Now.AddMs(ms);
    }

    // ---- Maintenance (ADR-013) ---------------------------------------------------------------------------

    /// <summary>
    /// Maintenance requested: the elevator drains. It takes no new assignment (Mode = Draining), hands every pending
    /// pickup back at once (released with reason Maintenance, so the dispatcher reassigns them now rather than after the
    /// riders are delivered), and keeps delivering its riders. Once it is idle and empty it parks where it is.
    /// </summary>
    /// <returns>False if maintenance was already requested or it is already parked.</returns>
    public bool RequestMaintenance()
    {
        if (_maintenanceRequested || State == ElevatorState.Maintenance) return false;
        _maintenanceRequested = true;
        Version++;
        Emit(new MaintenanceRequested(Now, Id));
        ReleaseAllPickups(AssignmentReason.Maintenance);
        DecideFromIdle(); // idle and empty: parks now
        return true;
    }

    /// <summary>Back in service: leaves the parked state (Maintenance → Idle), or cancels a drain still in progress.</summary>
    /// <returns>False if there was no maintenance to end.</returns>
    public bool EndMaintenance()
    {
        if (!_maintenanceRequested && State != ElevatorState.Maintenance) return false;
        _maintenanceRequested = false;
        Version++;
        if (State == ElevatorState.Maintenance) Fire(Trigger.ExitMaintenance);
        Emit(new MaintenanceExited(Now, Id));
        DecideFromIdle();
        return true;
    }

    /// <summary>Draining, idle and nobody on board: park here (the table's Idle → Maintenance edge).</summary>
    private void ParkIfDrained()
    {
        if (!_maintenanceRequested || _emergency || State != ElevatorState.Idle || _riders.Count > 0) return;
        Fire(Trigger.EnterMaintenance);
        Emit(new MaintenanceEntered(Now, Id, CurrentFloor));
    }

    // ---- Emergency stop (ADR-013) ------------------------------------------------------------------------

    /// <summary>
    /// Emergency stop. Takes effect now (logged with the floor it is at, or last passed) and stops the elevator at the
    /// next safe point:
    /// <list type="bullet">
    /// <item>moving: it finishes the floor segment it is in (it cannot stop between floors) and halts level with the
    /// next floor, doors closed (<see cref="Trigger.EmergencyHalt"/>);</item>
    /// <item>doors open: they close now; doors opening: they reverse and close, taking as long as they had opened
    /// (<see cref="Trigger.EmergencyClose"/>); doors closing: they finish closing;</item>
    /// <item>idle: it stays.</item>
    /// </list>
    /// The doors never open in an emergency: riders stay inside, protected. Pending pickups are handed back (reason
    /// Emergency) so other elevators serve them, and no new phase starts until <see cref="ClearEmergency"/>.
    /// </summary>
    /// <returns>False if already in an emergency, or parked for maintenance (already stopped, doors closed).</returns>
    public bool EmergencyStop()
    {
        if (_emergency || State == ElevatorState.Maintenance) return false;
        _emergency = true;
        Version++;
        Emit(new EmergencyStopped(Now, Id, CurrentFloor));
        ReleaseAllPickups(AssignmentReason.Emergency);
        switch (State)
        {
            case ElevatorState.DoorOpen:
                Fire(Trigger.EmergencyClose);
                _closingStartedAt = Now;
                StartPhase(_timing.DoorClosingMs);
                break;
            case ElevatorState.DoorOpening:
                // Reverse from where the doors are: open for x ms, they need x·(closing/opening) ms to close.
                var openingLeft = _stuck ? _frozenMs ?? 0 : _deadline!.Value.Ms - Now.Ms;
                var closing = (_timing.DoorOpeningMs - openingLeft) * _timing.DoorClosingMs / _timing.DoorOpeningMs;
                Fire(Trigger.EmergencyClose);
                _closingStartedAt = Now.AddMs(closing - _timing.DoorClosingMs); // as if closing had started that long ago
                StartPhase(closing);
                break;
            case ElevatorState.Idle:
                CommittedDirection = null;
                break;
            // Moving: halts at the next floor (OnFloorReached). Closing: finishes, then stays (DecideFromIdle).
        }
        return true;
    }

    /// <summary>
    /// Clears the emergency stop: normal decisions resume (draining resumes if maintenance was requested). If riders
    /// are headed for the floor where it stopped (it halted there, or its doors had not finished opening), the doors
    /// open here first.
    /// </summary>
    /// <returns>False if there was no emergency.</returns>
    public bool ClearEmergency()
    {
        if (!_emergency) return false;
        _emergency = false;
        Version++;
        Emit(new EmergencyCleared(Now, Id));
        if (State != ElevatorState.Idle) return true; // still moving or closing: carries on normally
        if (_riders.Any(r => r.DestinationFloor == CurrentFloor)) OpenDoor();
        else DecideFromIdle();
        RecoverIfHealthy();
        return true;
    }

    // ---- Fault (watchdog, ADR-013) -----------------------------------------------------------------------

    /// <summary>
    /// The dispatcher's watchdog saw no progress for <paramref name="silentForMs"/> while this elevator had work. It goes
    /// into Fault mode: logged (<see cref="FaultDetected"/>), every pending pickup handed back (reason Fault) so other
    /// elevators serve them, no new assignment. Riders stay on board; it still tries to deliver them, and it recovers
    /// (<see cref="FaultRecovered"/>) as soon as it moves again: a timed phase completes, or, with nothing to do, as soon
    /// as the drive is no longer stuck.
    /// </summary>
    /// <returns>False if already faulty or parked for maintenance. Always acknowledged with a new version, so the
    /// dispatcher gets a fresh snapshot either way.</returns>
    public bool DeclareFault(long silentForMs)
    {
        Version++;
        if (_faulted || State == ElevatorState.Maintenance) return false;
        _faulted = true;
        Emit(new FaultDetected(Now, Id, CurrentFloor, silentForMs));
        ReleaseAllPickups(AssignmentReason.Fault);
        RecoverIfHealthy();
        return true;
    }

    private void Recover()
    {
        _faulted = false;
        Version++;
        Emit(new FaultRecovered(Now, Id));
    }

    /// <summary>Faulty but able to move and with nothing in progress (idle, not stuck, not stopped by an emergency): recovered.</summary>
    private void RecoverIfHealthy()
    {
        if (_faulted && !_stuck && !_emergency && _deadline is null && State is ElevatorState.Idle or ElevatorState.Maintenance) Recover();
    }

    // ---- Stuck drive (fault injection, ADR-013) ---------------------------------------------------------

    /// <summary>True while the drive is stuck.</summary>
    public bool IsStuck => _stuck;

    /// <summary>
    /// The drive gets stuck: the current timed phase (travel or a door phase) stops progressing, and so does any phase
    /// started meanwhile. The controller keeps working (it takes messages and decides), but nothing physical happens.
    /// The snapshot shows no phase end while stuck. Not a state change: the state machine stays where it was.
    /// </summary>
    /// <returns>False if it was already stuck.</returns>
    public bool Stick()
    {
        if (_stuck) return false;
        _stuck = true;
        if (_deadline is { } d)
        {
            _frozenMs = d.Ms - Now.Ms;
            _deadline = null;
        }
        Version++;
        return true;
    }

    /// <summary>The drive works again: the frozen phase resumes with the time it had left.</summary>
    /// <returns>False if it was not stuck.</returns>
    public bool Unstick()
    {
        if (!_stuck) return false;
        _stuck = false;
        if (_frozenMs is { } left)
        {
            _deadline = Now.AddMs(left);
            _frozenMs = null;
            // Keep "phase end = closing start + closing time" true, so a reopen (and a projection) computes the doors' position right.
            if (State == ElevatorState.DoorClosing) _closingStartedAt = Now.AddMs(left - _timing.DoorClosingMs);
        }
        Version++;
        RecoverIfHealthy();
        return true;
    }

    private string NotServed(int floor) => $"elevator {Id} ({TypeName}) does not serve floor {floor}";
}
