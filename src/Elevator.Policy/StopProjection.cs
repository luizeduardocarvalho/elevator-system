using Elevator.Domain;

namespace Elevator.Policy;

/// <summary>
/// Fast route projection for cost-based dispatch (ADR-006). It computes exactly what <see cref="RouteProjection"/>
/// computes (the projected pickup time, the load at pickup, and the delay a new request adds to everyone already
/// riding or assigned), but it works per stop and per destination instead of per passenger. Its cost depends on the
/// number of floors, not on how many people are queued.
///
/// <para><b>How.</b> It replays the elevator's own mechanics and scheduling policy over a compact state:</para>
/// <list type="bullet">
/// <item>drop-off floors (a bit per floor);</item>
/// <item>pickup stops in registration order (at most two per floor);</item>
/// <item>riders as per-destination totals.</item>
/// </list>
/// <para>Each pickup stop carries its own per-destination totals, maintained incrementally by the elevator.</para>
///
/// <para><b>Added delay ΔD.</b> Everyone who boards at the same stop and goes to the same floor gets off together, so
/// they form a group with one completion time. Inside a stop people board in arrival order and the new request is
/// always last. So in both projections the people who board form a prefix of the same arrival-ordered list. Per
/// destination, the people who board in both projections are therefore the first min(n_without, n_with). Then:</para>
/// <para>ΔD = Σ_groups [ min(n_w, n_x)·(t_x − t_w) + (n_w − min(n_w, n_x))·(horizon_x − t_w) ]</para>
/// <para>t_w and t_x are the group's drop-off times without and with the new request. horizon_x stands in for someone
/// who would not finish within the projection's horizon.</para>
///
/// <para>Only when a stop cannot take everyone (capacity) does it look at individual requests, and then at most up to
/// the elevator's capacity of them.</para>
///
/// <para><see cref="RouteProjection"/> is kept as the reference oracle: <c>StopProjectionEquivalenceTests</c>
/// checks that the two agree exactly on random elevators.</para>
/// </summary>
public sealed class StopProjection(ISchedulingPolicy scheduling, ElevatorTiming timing, long horizonMs = 3_600_000) : IEtaEstimator
{
    /// <summary>Drop-off time and count of each group, plus how many requests boarded from each pickup stop.</summary>
    public sealed class Outcome
    {
        internal readonly Dictionary<(int Source, int Dest), (int Count, long DoneMs)> Groups = [];

        public SimTime Horizon { get; internal set; }

        public SimTime? PickupAt { get; internal set; }

        public int LoadAtPickup { get; internal set; }

        /// <summary>Policy decisions and stops processed: the measure of work (independent of the backlog).</summary>
        public int Steps { get; internal set; }

        public string Describe() => string.Join(" ", Groups.OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Value.Count}@{g.Value.DoneMs}"));
    }

    public SimTime? EstimatePickup(ElevatorSnapshot snapshot, Request request, SimTime now) => Project(snapshot, request, now).PickupAt;

    public Outcome Project(ElevatorSnapshot snapshot, Request? extra, SimTime now)
    {
        var start = snapshot.At > now ? snapshot.At : now;
        var run = new Run(snapshot, scheduling, timing, extra);
        run.AdvanceTo(start.Ms);
        if (extra is not null) run.Insert(extra);
        var horizon = start.AddMs(horizonMs);
        run.AdvanceTo(horizon.Ms);
        run.Result.Horizon = horizon;
        return run.Result;
    }

    /// <summary>
    /// Total extra milliseconds that the passengers who finish within <paramref name="without"/>'s horizon would spend
    /// before their drop-off in <paramref name="with"/> (the formula in the class summary).
    /// </summary>
    public static long AddedDelayMs(Outcome without, Outcome with)
    {
        long added = 0;
        foreach (var (key, w) in without.Groups)
        {
            if (w.DoneMs < 0) continue; // did not finish within the horizon: not counted (as in RouteProjection)
            var x = with.Groups.GetValueOrDefault(key, (0, -1));
            var both = Math.Min(w.Count, x.Count);
            var xDone = x.DoneMs >= 0 ? x.DoneMs : with.Horizon.Ms;
            added += both * (xDone - w.DoneMs) + (w.Count - both) * (with.Horizon.Ms - w.DoneMs);
        }
        return added;
    }

    private sealed class Pickup(int source, HallCall call)
    {
        public int Source { get; } = source; // index in the snapshot's hall calls (stable across both projections)
        public int Floor { get; } = call.Floor;
        public Direction Direction { get; } = call.Direction;
        public SimTime RegisteredAt { get; } = call.RegisteredAt;
        public HallCall Call { get; } = call;
        public Request? Extra { get; set; }
        public bool HasRequests => Call.Requests.Count > 0 || Extra is not null;
    }

    /// <summary>The elevator's mechanics (see <see cref="Domain.Elevator"/>) replayed over the compact state.</summary>
    private sealed class Run
    {
        private readonly ISchedulingPolicy _policy;
        private readonly ElevatorTiming _t;
        private readonly int _floors, _capacity, _id;
        private readonly double _threshold;
        private readonly Request? _extra;
        private readonly bool[] _carCalls;
        private readonly List<Pickup> _pickups = [];
        private readonly List<(int Source, int Dest)>[] _aboardByDest; // groups on board, by drop-off floor
        private readonly int[] _loadByDest;
        private int _load;
        private int _floor;
        private ElevatorState _state;
        private Direction? _dir;
        private long _now;
        private long? _deadline;
        private long _closingStartedAt;
        private bool _level;
        private int _nextSource;

        public Outcome Result { get; } = new();

        public Run(ElevatorSnapshot s, ISchedulingPolicy policy, ElevatorTiming timing, Request? extra)
        {
            _policy = policy;
            _t = timing;
            _floors = s.Floors;
            _capacity = s.Capacity;
            _threshold = s.BypassThreshold;
            _id = s.ElevatorId;
            _extra = extra;
            _floor = s.CurrentFloor;
            _state = s.State;
            _dir = s.CommittedDirection;
            _now = s.At.Ms;
            _deadline = s.PhaseEndsAt?.Ms;
            _closingStartedAt = s.ClosingStartedAt.Ms;
            _level = !s.IsMoving;

            _carCalls = new bool[_floors + 2];
            foreach (var c in s.CarCalls) _carCalls[c] = true;
            for (var i = 0; i < s.HallCalls.Count; i++) _pickups.Add(new Pickup(i, s.HallCalls[i]));
            _nextSource = s.HallCalls.Count;

            _aboardByDest = new List<(int, int)>[_floors + 2];
            _loadByDest = new int[_floors + 2];
            var riders = s.RiderTotals;
            for (var d = 1; d <= _floors && d < riders.Span; d++)
            {
                if (riders.CountTo(d) == 0) continue;
                Board(source: -1, d, riders.CountTo(d), riders.LoadTo(d));
            }
        }

        // ---- time ----

        public void AdvanceTo(long until)
        {
            DecideFromIdle(); // Resume()
            while (_deadline is { } d && d <= until)
            {
                _now = d;
                _deadline = null;
                OnPhaseEnded();
            }
            if (until > _now) _now = until;
        }

        private void OnPhaseEnded()
        {
            Result.Steps++;
            switch (_state)
            {
                case ElevatorState.MovingUp or ElevatorState.MovingDown:
                    var direction = _state == ElevatorState.MovingUp ? Direction.Up : Direction.Down;
                    _floor += direction == Direction.Up ? 1 : -1;
                    _level = true;
                    if (ShouldStopHere(direction))
                    {
                        OpenDoor();
                        return;
                    }
                    _level = false;
                    _deadline = _now + _t.FloorTravelMs;
                    break;
                case ElevatorState.DoorOpening:
                    _state = ElevatorState.DoorOpen;
                    _dir ??= _pickups.FirstOrDefault(p => p.Floor == _floor)?.Direction;
                    Alight();
                    AnswerHere();
                    _deadline = _now + _t.DoorDwellMs;
                    break;
                case ElevatorState.DoorOpen:
                    _state = ElevatorState.DoorClosing;
                    _closingStartedAt = _now;
                    _deadline = _now + _t.DoorClosingMs;
                    break;
                case ElevatorState.DoorClosing:
                    _state = ElevatorState.Idle;
                    _level = true;
                    DecideFromIdle();
                    break;
            }
        }

        private bool ShouldStopHere(Direction direction)
        {
            if (_carCalls[_floor]) return true;
            if (_floor == Floors.Lowest || _floor == _floors) return true;
            var target = Decide().Target;
            return target is null || target == _floor || !direction.IsAhead(_floor, target.Value);
        }

        private void DecideFromIdle()
        {
            if (_state != ElevatorState.Idle) return;
            var target = Decide().Target;
            if (target is null) _dir = null;
            else if (target == _floor) OpenDoor();
            else StartMoving(target > _floor ? Direction.Up : Direction.Down);
        }

        private void StartMoving(Direction d)
        {
            _state = d == Direction.Up ? ElevatorState.MovingUp : ElevatorState.MovingDown;
            _dir = d;
            _level = false;
            _deadline = _now + _t.FloorTravelMs;
        }

        private void OpenDoor()
        {
            if (_state == ElevatorState.DoorClosing)
            {
                _state = ElevatorState.DoorOpening;
                _deadline = _now + (_now - _closingStartedAt) * _t.DoorOpeningMs / _t.DoorClosingMs;
                return;
            }
            _carCalls[_floor] = false;
            var lantern = LanternForStop();
            _state = ElevatorState.DoorOpening;
            _level = true;
            _dir = lantern;
            _deadline = _now + _t.DoorOpeningMs;
        }

        private Direction? LanternForStop()
        {
            var decision = Decide();
            if (decision.Target is not { } target) return null;
            if (target != _floor) return Directions.Between(_floor, target);
            return decision.ServeDirection ?? _pickups.FirstOrDefault(p => p.Floor == _floor)?.Direction;
        }

        // ---- people ----

        private void Alight()
        {
            // Everyone headed here gets off, including the new request (which is not part of any group).
            _load -= _loadByDest[_floor];
            _loadByDest[_floor] = 0;
            if (_aboardByDest[_floor] is not { Count: > 0 } groups) return;
            foreach (var key in groups)
            {
                var g = Result.Groups[key];
                Result.Groups[key] = (g.Count, _now);
            }
            groups.Clear();
        }

        private void AnswerHere()
        {
            var index = _pickups.FindIndex(p => p.Floor == _floor && p.Direction == _dir);
            if (index < 0) return;
            var stop = _pickups[index];
            _pickups.RemoveAt(index);
            Result.Steps++;
            if (!stop.HasRequests) return;

            var totals = stop.Call.Totals;
            var extraLoad = stop.Extra?.Load ?? 0;
            if (_load + totals.TotalLoad + extraLoad <= _capacity)
            {
                // Everyone fits: board per destination from the totals, no per-passenger work.
                for (var d = 1; d < totals.Span; d++)
                    if (totals.CountTo(d) > 0) Board(stop.Source, d, totals.CountTo(d), totals.LoadTo(d));
                if (stop.Extra is { } x) BoardExtra(x);
            }
            else
            {
                // Not everyone fits: walk the arrival order (as the passengers do), skipping whoever does not fit.
                // Stops once the elevator is full, so at most about `capacity` requests are looked at.
                foreach (var r in stop.Call.Requests)
                {
                    if (_load >= _capacity) break;
                    if (_load + r.Load > _capacity) continue;
                    Board(stop.Source, r.DestinationFloor, 1, r.Load);
                }
                if (stop.Extra is { } x && _load + x.Load <= _capacity) BoardExtra(x);
            }

            if (_load >= _threshold * _capacity) ReleaseAll();
        }

        private void BoardExtra(Request x)
        {
            Result.PickupAt = new SimTime(_now);
            Result.LoadAtPickup = _load;
            AddDropOff(x.DestinationFloor);
            _load += x.Load;
            _loadByDest[x.DestinationFloor] += x.Load;
        }

        private void Board(int source, int dest, int count, int load)
        {
            var key = (source, dest);
            var g = Result.Groups.GetValueOrDefault(key, (0, -1));
            if (g.Item1 == 0) (_aboardByDest[dest] ??= []).Add(key);
            Result.Groups[key] = (g.Item1 + count, -1);
            _load += load;
            _loadByDest[dest] += load;
            if (source >= 0) AddDropOff(dest); // riders already on board: their drop-offs are in the snapshot's car calls
        }

        /// <summary>Elevator.AddRequest: a drop-off for the floor it stands at is ignored.</summary>
        private void AddDropOff(int floor)
        {
            if (floor == _floor && _level) return;
            _carCalls[floor] = true;
        }

        /// <summary>Full-load bypass: every pending pickup that has requests is handed back.</summary>
        private void ReleaseAll()
        {
            if (_pickups.FirstOrDefault(p => p.Extra is not null) is not null) Result.LoadAtPickup = _load;
            _pickups.RemoveAll(p => p.HasRequests);
        }

        public void Insert(Request r)
        {
            var existing = _pickups.FirstOrDefault(p => p.Floor == r.PickupFloor && p.Direction == r.Direction);
            if (existing is not null)
            {
                existing.Extra = r; // joins the stop, last in arrival order
                return;
            }

            _pickups.Add(new Pickup(_nextSource++, new HallCall(r.PickupFloor, r.Direction, new SimTime(_now))) { Extra = r });
            if (_state == ElevatorState.Idle)
            {
                DecideFromIdle();
                return;
            }

            var hereInDoorCycle = r.PickupFloor == _floor && _state is ElevatorState.DoorOpening or ElevatorState.DoorOpen or ElevatorState.DoorClosing;
            if (!hereInDoorCycle) return;
            _dir ??= r.Direction;
            if (_dir != r.Direction) return;
            switch (_state)
            {
                case ElevatorState.DoorOpen:
                    AnswerHere();
                    _deadline = _now + _t.DoorDwellMs;
                    break;
                case ElevatorState.DoorClosing:
                    OpenDoor();
                    break;
            }
        }

        // ---- the scheduling policy, on a compact snapshot (at most `floors` drop-offs and 2·floors pickups) ----

        private PolicyDecision Decide()
        {
            Result.Steps++;
            var carCalls = new List<int>();
            for (var f = 1; f <= _floors; f++) if (_carCalls[f]) carCalls.Add(f);
            var pickups = _pickups.Select(p => new HallCall(p.Floor, p.Direction, p.RegisteredAt)).ToList();
            return _policy.Decide(ElevatorSnapshot.ForPolicy(_floor, _state, _dir, carCalls, pickups, _floors, _id));
        }
    }
}
