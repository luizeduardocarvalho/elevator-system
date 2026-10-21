using Elevator.Domain;
using Elevator.Domain.Events;

namespace Elevator.Metrics;

/// <summary>Where a waiting passenger's time goes (ADR-008). Every instant of [t_call, t_board] is in exactly one bucket.</summary>
public enum WaitBucket
{
    /// <summary>Not yet assigned for the first time.</summary>
    Dispatch,
    /// <summary>Unassigned after a stale reject or a rebalance move: the dispatcher's view was wrong or improved.</summary>
    Reassignment,
    /// <summary>
    /// Waiting because of a full elevator: the assigned elevator is full and bypassing pickups until its next trip
    /// (NextTrip mode), or it handed the pickup back / rejected it for capacity (Defer mode).
    /// </summary>
    BypassedFull,
    /// <summary>Left behind at an open door because the elevator was full (and still waiting for room); or the
    /// assigned elevator is in maintenance / rejected for maintenance.</summary>
    Blocked,
    /// <summary>Assigned elevator is completing a stop that was already in progress when the assignment was accepted.</summary>
    FinishingDoorCycle,
    /// <summary>Assigned elevator is stopped at another floor (a stop that started after the assignment).</summary>
    ServingOthers,
    /// <summary>Assigned elevator is idle with doors closed.</summary>
    IdleStart,
    /// <summary>Assigned elevator is moving and getting closer, or opening its doors at the passenger's floor.</summary>
    TravelToward,
    /// <summary>Assigned elevator is moving away (finishing its sweep first).</summary>
    TravelAway,
}

/// <summary>Everything that happened to one passenger. Times are sim time; wait buckets are in sim seconds.</summary>
public sealed record PassengerRecord(
    int Id,
    int From,
    int To,
    SimTime CalledAt,
    SimTime? FirstAssignedAt,
    int? Elevator,
    int Assignments,
    int Rejections,
    int LeftBehind,
    SimTime? ElevatorArrivedAt,
    SimTime? BoardedAt,
    SimTime? AlightedAt,
    IReadOnlyDictionary<WaitBucket, double> WaitBuckets)
{
    public double? WaitS => BoardedAt is { } b ? (b.Ms - CalledAt.Ms) / 1000.0 : null;

    public double? RideS => (BoardedAt, AlightedAt) is ({ } b, { } a) ? (a.Ms - b.Ms) / 1000.0 : null;

    public double? JourneyS => AlightedAt is { } a ? (a.Ms - CalledAt.Ms) / 1000.0 : null;

    public bool Delivered => AlightedAt is not null;

    /// <summary>Accepted assignments after the first one.</summary>
    public int Reassignments => Math.Max(0, Assignments - 1);
}

/// <summary>
/// Rebuilds every passenger's lifecycle and wait attribution from the event log alone.
///
/// Attribution is exact: between two consecutive event times nothing changes, so the whole interval goes to the
/// bucket given by the state at its start (the same as summing infinitely small ticks). The state is the
/// passenger's assignment status plus, when assigned, the assigned elevator's state relative to the passenger.
/// By construction the buckets of a passenger sum exactly to their wait.
/// </summary>
public static class PassengerLifecycle
{
    private enum Status { Dispatch, Reassignment, BypassedFull, Blocked, Assigned }

    private sealed class ElevatorView
    {
        public int Riders;
        public ElevatorState State = ElevatorState.Idle;
        public int Floor = Floors.Lowest;
        public long DoorCycleStartedMs = -1;
        public (long Ms, int Floor) LastArrival = (-1, 0);
    }

    private sealed class Tracker(int id, int from, int to, SimTime calledAt)
    {
        public readonly int Id = id, From = from, To = to;
        public readonly SimTime CalledAt = calledAt;
        public Status Status = Status.Dispatch;
        public int? Elevator;
        public long AssignedAtMs;
        public SimTime? FirstAssignedAt, ElevatorArrivedAt, BoardedAt, AlightedAt;
        public int Assignments, Rejections, LeftBehind;
        public readonly double[] Buckets = new double[Enum.GetValues<WaitBucket>().Length];

        public PassengerRecord ToRecord() => new(Id, From, To, CalledAt, FirstAssignedAt, Elevator, Assignments, Rejections, LeftBehind,
            ElevatorArrivedAt, BoardedAt, AlightedAt,
            Enum.GetValues<WaitBucket>().ToDictionary(b => b, b => Buckets[(int)b] / 1000.0));
    }

    public static IReadOnlyList<PassengerRecord> Analyze(IEnumerable<DomainEvent> events)
    {
        var elevators = new Dictionary<int, ElevatorView>();
        var passengers = new Dictionary<int, Tracker>();
        var waiting = new List<Tracker>();
        long lastMs = 0;
        var fullAt = double.PositiveInfinity; // riders at which an elevator bypasses pickups (persons: load 1 each)

        ElevatorView View(int id) => elevators.TryGetValue(id, out var v) ? v : elevators[id] = new ElevatorView();

        foreach (var e in events)
        {
            if (e.At.Ms > lastMs)
            {
                var span = e.At.Ms - lastMs;
                foreach (var p in waiting) p.Buckets[(int)BucketOf(p, p.Elevator is { } id ? View(id) : null, fullAt)] += span;
                lastMs = e.At.Ms;
            }

            switch (e)
            {
                case SimulationStarted started:
                    fullAt = started.Capacity == int.MaxValue ? double.PositiveInfinity : started.BypassThreshold * started.Capacity;
                    break;
                case PassengerArrived a:
                    var t = new Tracker(a.PassengerId, a.From, a.To, a.At);
                    passengers[a.PassengerId] = t;
                    waiting.Add(t);
                    break;
                case AssignmentAccepted acc when passengers.GetValueOrDefault(acc.RequestId) is { BoardedAt: null } p:
                    p.Status = Status.Assigned;
                    p.Elevator = acc.ElevatorId;
                    p.AssignedAtMs = acc.At.Ms;
                    p.FirstAssignedAt ??= acc.At;
                    p.Assignments++;
                    break;
                case AssignmentRejected rej when passengers.GetValueOrDefault(rej.RequestId) is { BoardedAt: null } p:
                    p.Rejections++;
                    // The reason decides where the time goes: a stale view is a dispatch problem; a full or
                    // out-of-service elevator is a capacity/availability problem.
                    p.Status = rej.Reason switch
                    {
                        AssignmentReason.Maintenance => Status.Blocked,
                        AssignmentReason.Capacity => p.Status == Status.Blocked ? Status.Blocked : Status.BypassedFull,
                        _ => Status.Reassignment,
                    };
                    p.Elevator = null;
                    break;
                case AssignmentReleased rel when passengers.GetValueOrDefault(rel.RequestId) is { BoardedAt: null } p && p.Elevator == rel.ElevatorId:
                    p.Status = rel.Reason == AssignmentReason.BypassedFull ? Status.BypassedFull : Status.Reassignment;
                    p.Elevator = null;
                    break;
                case PassengerLeftBehind left when passengers.GetValueOrDefault(left.PassengerId) is { } p:
                    p.LeftBehind++;
                    p.Status = Status.Blocked;
                    p.Elevator = null;
                    break;
                case PassengerBoarded b when passengers.GetValueOrDefault(b.PassengerId) is { } p:
                    View(b.ElevatorId).Riders++;
                    p.BoardedAt = b.At;
                    p.Elevator = b.ElevatorId;
                    var arrival = View(b.ElevatorId).LastArrival;
                    p.ElevatorArrivedAt = arrival.Floor == b.Floor && arrival.Ms >= 0 ? new SimTime(arrival.Ms) : b.At;
                    waiting.Remove(p);
                    break;
                case PassengerAlighted al when passengers.GetValueOrDefault(al.PassengerId) is { } p:
                    View(al.ElevatorId).Riders--;
                    p.AlightedAt = al.At;
                    break;
                case StateChanged sc:
                    var view = View(sc.ElevatorId);
                    view.State = sc.To;
                    view.Floor = sc.Floor;
                    if (sc.Trigger == Trigger.ArriveAndOpen)
                    {
                        view.DoorCycleStartedMs = sc.At.Ms;
                        view.LastArrival = (sc.At.Ms, sc.Floor);
                    }
                    break;
                case FloorReached fr:
                    View(fr.ElevatorId).Floor = fr.Floor;
                    break;
            }
        }

        return passengers.Values.OrderBy(p => p.Id).Select(p => p.ToRecord()).ToList();
    }

    private static WaitBucket BucketOf(Tracker p, ElevatorView? e, double fullAt)
    {
        switch (p.Status)
        {
            case Status.Dispatch: return WaitBucket.Dispatch;
            case Status.Reassignment: return WaitBucket.Reassignment;
            case Status.BypassedFull: return WaitBucket.BypassedFull;
            case Status.Blocked: return WaitBucket.Blocked;
        }

        if (e!.Riders >= fullAt) return WaitBucket.BypassedFull; // the assigned elevator is full: next trip (ADR-010)

        switch (e.State)
        {
            case ElevatorState.Maintenance:
                return WaitBucket.Blocked;
            case ElevatorState.Idle:
                return WaitBucket.IdleStart;
            case ElevatorState.MovingUp:
                return p.From > e.Floor ? WaitBucket.TravelToward : WaitBucket.TravelAway;
            case ElevatorState.MovingDown:
                return p.From < e.Floor ? WaitBucket.TravelToward : WaitBucket.TravelAway;
            default: // door cycle
                if (e.DoorCycleStartedMs < p.AssignedAtMs) return WaitBucket.FinishingDoorCycle;
                if (e.State == ElevatorState.DoorOpening && e.Floor == p.From) return WaitBucket.TravelToward;
                return WaitBucket.ServingOthers;
        }
    }
}
