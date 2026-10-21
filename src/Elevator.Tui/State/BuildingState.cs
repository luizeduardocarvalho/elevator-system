using Elevator.Domain;
using Elevator.Domain.Events;

namespace Elevator.Tui.State;

/// <summary>
/// What the screen shows, rebuilt by folding the event log (ADR-007, ADR-011): the only input is events, in log
/// order. Live, the TUI folds the engine's new log entries after each step; in <c>replay</c> it folds the file. Both
/// use this one class, so a replay shows exactly what the live run showed at the same sim time.
///
/// It is proved against the engine's own read-only views (BuildingStateTests): at sampled times every elevator's
/// floor, state, shown direction, riders and mode, and the waiting counts, equal <c>SimEngine.Elevators</c> and
/// <c>SimEngine.Waiting</c>.
///
/// Single-threaded and mutable on purpose: the loop owns it, and the renderer only sees the immutable ScreenModel
/// built from it.
/// </summary>
public sealed class BuildingState
{
    /// <summary>How many recent log items the event tail keeps.</summary>
    public const int TailCapacity = 256;

    /// <summary>A wait above this is "long" in the metrics panel (sim seconds).</summary>
    public const double LongWaitS = 60;

    private sealed class PassengerView(int from, Direction direction, SimTime calledAt, int load = 1)
    {
        public readonly int Load = load; // load units (v3.1 PassengerArrived.Load; a person is 1)
        public readonly int From = from;
        public readonly Direction Direction = direction;
        public readonly SimTime CalledAt = calledAt;
        public bool Boarded;
        public int Assignments;
        public SimTime? OpsReleasedAt;
    }

    private readonly List<ElevatorView> _elevators = [];
    private readonly Dictionary<(int Floor, Direction Direction), int> _waiting = [];
    private readonly Dictionary<int, PassengerView> _passengers = [];
    private readonly List<double> _waits = [];
    private readonly Queue<LogItem> _tail = new();
    private DispatchEvaluated? _lastEvaluation;

    public string Scenario { get; private set; } = "";
    public int Seed { get; private set; }
    public int Floors { get; private set; } = 2;
    public int Capacity { get; private set; } = int.MaxValue;
    public string Scheduling { get; private set; } = "";
    public string Dispatch { get; private set; } = "";
    public int ExpectedPassengers { get; private set; }
    public bool Ended { get; private set; }

    /// <summary>Sim time shown: the latest event's, or later when the clock moved without events (<see cref="AdvanceTo"/>).</summary>
    public SimTime Now { get; private set; }

    public IReadOnlyList<ElevatorView> Elevators => _elevators;

    /// <summary>People waiting per floor and direction (same shape as <c>SimEngine.Waiting</c>: no zero entries).</summary>
    public IReadOnlyDictionary<(int Floor, Direction Direction), int> Waiting => _waiting;

    /// <summary>Wait (call → boarding) of every passenger who has boarded, in sim seconds, in boarding order.</summary>
    public IReadOnlyList<double> Waits => _waits;

    public int Arrived { get; private set; }
    public int Delivered { get; private set; }

    /// <summary>Accepted assignments after a passenger's first (same definition as the run report).</summary>
    public int Reassignments { get; private set; }

    /// <summary>Pickups handed back for maintenance, emergency or a fault (the Hard stage's reassignments).</summary>
    public int OpsReassignments { get; private set; }

    /// <summary>Sim time those passengers spent unassigned between an ops hand-back and their next assignment.</summary>
    public long OpsPenaltyMs { get; private set; }

    public int CommandsApplied { get; private set; }
    public int AccessDenials { get; private set; }

    /// <summary>The most recent log items, oldest first (floor-by-floor noise and already-explained proposals left out).</summary>
    public IReadOnlyList<LogItem> Tail => [.. _tail];

    /// <summary>Moves the clock without events (live: the engine's time; replay: the cursor). Never backwards.</summary>
    public void AdvanceTo(SimTime now)
    {
        if (now > Now) Now = now;
    }

    public void Apply(LogItem item)
    {
        if (item.Known is { } e) Apply(e);
        else if (item.Unknown is { } u)
        {
            AdvanceTo(u.At);
            AddToTail(u);
        }
    }

    public void Apply(DomainEvent e)
    {
        AdvanceTo(e.At);
        if (ShowInTail(e)) AddToTail(e);
        _lastEvaluation = e as DispatchEvaluated;

        switch (e)
        {
            case SimulationStarted s:
                Scenario = s.Scenario;
                Seed = s.Seed;
                Floors = s.Floors;
                Capacity = s.Capacity;
                Scheduling = s.Scheduling;
                Dispatch = s.Dispatch;
                ExpectedPassengers = s.Passengers;
                for (var i = 0; i < s.Elevators; i++) View(i);
                foreach (var f in s.Fleet ?? []) // v3.1: a typed fleet carries each elevator's own type and capacity
                    if (View(f.ElevatorId) is { } typed)
                    {
                        typed.TypeName = f.Type;
                        typed.Capacity = f.Capacity;
                    }
                break;
            case SimulationEnded:
                Ended = true;
                break;

            case PassengerArrived a:
                if (_passengers.ContainsKey(a.PassengerId)) break; // a re-registration: already counted as waiting
                if (Directions.Between(a.From, a.To) is not { } direction) break;
                _passengers[a.PassengerId] = new PassengerView(a.From, direction, a.At, a.LoadUnits);
                Arrived++;
                Increment((a.From, direction), +1);
                break;
            case PassengerBoarded b:
                if (_passengers.GetValueOrDefault(b.PassengerId) is { Boarded: false } boarding)
                {
                    boarding.Boarded = true;
                    Increment((boarding.From, boarding.Direction), -1);
                    _waits.Add((b.At.Ms - boarding.CalledAt.Ms) / 1000.0);
                }
                if (View(b.ElevatorId) is { } boardedOn) boardedOn.Riders += _passengers.GetValueOrDefault(b.PassengerId)?.Load ?? 1;
                break;
            case PassengerAlighted al:
                if (View(al.ElevatorId) is { } alightedFrom)
                    alightedFrom.Riders = Math.Max(0, alightedFrom.Riders - (_passengers.GetValueOrDefault(al.PassengerId)?.Load ?? 1));
                Delivered++;
                break;

            case StateChanged sc when View(sc.ElevatorId) is { } v:
                v.SetState(sc.To, sc.At);
                v.Floor = sc.Floor;
                break;
            case FloorReached fr when View(fr.ElevatorId) is { } v:
                v.Floor = fr.Floor;
                break;
            case DoorsOpened d when View(d.ElevatorId) is { } v:
                v.Lantern = d.Lantern;
                break;
            case HallCallAnswered h when View(h.ElevatorId) is { } v:
                v.Lantern = h.Direction;
                break;

            case MaintenanceRequested m when View(m.ElevatorId) is { } v:
                v.SetMode(ElevatorMode.Draining, m.At);
                break;
            case MaintenanceEntered m when View(m.ElevatorId) is { } v:
                v.SetMode(ElevatorMode.Maintenance, m.At);
                break;
            case MaintenanceExited m when View(m.ElevatorId) is { } v:
                v.SetMode(ElevatorMode.Normal, m.At);
                break;
            case EmergencyStopped es when View(es.ElevatorId) is { } v:
                v.EmergencyStops++;
                v.Interrupt(ElevatorMode.Emergency, es.At);
                break;
            case EmergencyCleared ec when View(ec.ElevatorId) is { } v:
                v.Resume(ElevatorMode.Emergency, ec.At);
                break;
            case FaultDetected fd when View(fd.ElevatorId) is { } v:
                v.Faults++;
                v.Interrupt(ElevatorMode.Fault, fd.At);
                break;
            case FaultRecovered fr when View(fr.ElevatorId) is { } v:
                v.Resume(ElevatorMode.Fault, fr.At);
                break;
            case FaultCommandApplied:
                CommandsApplied++;
                break;
            case AccessDenied denied:
                AccessDenials++;
                // No elevator named = every elevator refused (ADR-012): the engine drops the passenger from the
                // waiting set (PassengersHandler, RequestRefused), so the fold stops counting them too.
                if (denied.ElevatorId is null && _passengers.Remove(denied.RequestId, out var refused) && !refused.Boarded)
                    Increment((refused.From, refused.Direction), -1);
                break;

            case AssignmentAccepted acc when _passengers.GetValueOrDefault(acc.RequestId) is { Boarded: false } p:
                if (++p.Assignments > 1) Reassignments++;
                if (p.OpsReleasedAt is { } releasedAt)
                {
                    OpsPenaltyMs += acc.At.Ms - releasedAt.Ms;
                    p.OpsReleasedAt = null;
                }
                break;
            case AssignmentReleased rel when rel.Reason is AssignmentReason.Maintenance or AssignmentReason.Fault or AssignmentReason.Emergency:
                OpsReassignments++;
                if (_passengers.GetValueOrDefault(rel.RequestId) is { Boarded: false } released) released.OpsReleasedAt ??= rel.At;
                break;
        }
    }

    /// <summary>
    /// FloorReached is one line per floor travelled (the elevator glyph already shows it), and a proposal right after
    /// its own DispatchEvaluated repeats the ASSIGN line; both would only push useful lines out of the tail.
    /// </summary>
    private bool ShowInTail(DomainEvent e) => e switch
    {
        FloorReached => false,
        AssignmentProposed p => _lastEvaluation is not { } ev || ev.RequestId != p.RequestId || ev.WinnerId != p.ElevatorId,
        _ => true,
    };

    private void AddToTail(LogItem item)
    {
        if (_tail.Count == TailCapacity) _tail.Dequeue();
        _tail.Enqueue(item);
    }

    private void Increment((int, Direction) key, int delta)
    {
        var count = _waiting.GetValueOrDefault(key) + delta;
        if (count > 0) _waiting[key] = count;
        else _waiting.Remove(key);
    }

    /// <summary>The view of elevator <paramref name="id"/>, growing the list if the log names one not announced at start.</summary>
    private ElevatorView? View(int id)
    {
        if (id is < 0 or > 1_000) return null; // a corrupt id: ignore rather than allocate
        while (_elevators.Count <= id) _elevators.Add(new ElevatorView(_elevators.Count, Now));
        return _elevators[id];
    }
}

/// <summary>One elevator as folded from the log. Times are sim time.</summary>
public sealed class ElevatorView(int id, SimTime createdAt)
{
    private long _busyMs;
    private SimTime _stateSince = createdAt;
    private SimTime _modeSince = createdAt;
    private ElevatorMode _modeBefore = ElevatorMode.Normal;
    private readonly long[] _modeMs = new long[Enum.GetValues<ElevatorMode>().Length];

    public int Id { get; } = id;
    public int Floor { get; internal set; } = Domain.Floors.Lowest;
    public ElevatorState State { get; private set; } = ElevatorState.Idle;
    public ElevatorMode Mode { get; private set; } = ElevatorMode.Normal;
    /// <summary>Load on board in load units (a person is 1; freight per v3.1 PassengerArrived.Load).</summary>
    public int Riders { get; internal set; }
    /// <summary>Its own capacity when the log describes a typed fleet (v3.1); null = the building's.</summary>
    public int? Capacity { get; internal set; }
    /// <summary>Elevator type from the fleet (v3.1); "standard" otherwise.</summary>
    public string TypeName { get; internal set; } = "standard";
    public int EmergencyStops { get; internal set; }
    public int Faults { get; internal set; }

    /// <summary>The lantern from the last DoorsOpened (or a hall call answered while open).</summary>
    public Direction? Lantern { get; internal set; }

    /// <summary>Direction to draw: travel direction while moving; the lantern while doors are open or closing; else none.</summary>
    public Direction? ShownDirection => State switch
    {
        ElevatorState.MovingUp => Direction.Up,
        ElevatorState.MovingDown => Direction.Down,
        ElevatorState.DoorOpen or ElevatorState.DoorClosing => Lantern,
        _ => null,
    };

    private static bool IsBusy(ElevatorState s) => s is not (ElevatorState.Idle or ElevatorState.Maintenance);

    /// <summary>Share of [0, now] spent doing something (moving or in a door cycle): not idle, not parked.</summary>
    public double Utilization(SimTime now)
    {
        if (now.Ms <= 0) return 0;
        var busy = _busyMs + (IsBusy(State) ? Math.Max(0, now.Ms - _stateSince.Ms) : 0);
        return (double)busy / now.Ms;
    }

    /// <summary>Total sim ms spent in <paramref name="mode"/>, including the current stretch up to <paramref name="now"/>.</summary>
    public long ModeMs(ElevatorMode mode, SimTime now) =>
        _modeMs[(int)mode] + (Mode == mode ? Math.Max(0, now.Ms - _modeSince.Ms) : 0);

    internal void SetState(ElevatorState next, SimTime at)
    {
        if (IsBusy(State)) _busyMs += Math.Max(0, at.Ms - _stateSince.Ms);
        State = next;
        _stateSince = at;
    }

    internal void SetMode(ElevatorMode next, SimTime at)
    {
        _modeMs[(int)Mode] += Math.Max(0, at.Ms - _modeSince.Ms);
        Mode = next;
        _modeSince = at;
    }

    /// <summary>Emergency or fault: remember the mode it interrupted (e.g. Draining) to return to it afterwards.</summary>
    internal void Interrupt(ElevatorMode mode, SimTime at)
    {
        if (Mode is not (ElevatorMode.Emergency or ElevatorMode.Fault)) _modeBefore = Mode;
        SetMode(mode, at);
    }

    internal void Resume(ElevatorMode from, SimTime at)
    {
        if (Mode == from) SetMode(_modeBefore, at);
    }
}
