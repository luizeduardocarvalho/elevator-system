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
public sealed class StopProjection(ISchedulingPolicy scheduling, ElevatorTiming timing, long horizonMs = StopProjection.DefaultHorizonMs) : IEtaEstimator
{
    public sealed class Outcome
    {
        internal Outcome(int sources, int floors)
        {
            Width = floors + 2;
            Counts = new int[(sources + 1) * Width];
            Done = new long[(sources + 1) * Width];
            Array.Fill(Done, -1);
        }

        internal int Width { get; }

        internal int[] Counts { get; }

        internal long[] Done { get; }

        internal int Slot(int source, int dest) => (source + 1) * Width + dest;

        public SimTime Horizon { get; internal set; }

        public SimTime? PickupAt { get; internal set; }

        public int LoadAtPickup { get; internal set; }

        /// <summary>Policy decisions made: the measure of work (bounded by the number of stops, so by the floors, not the backlog).</summary>
        public int Steps { get; internal set; }

        public string Describe() => string.Join(" ", Enumerable.Range(0, Counts.Length).Where(i => Counts[i] > 0)
            .Select(i => $"({i / Width - 1}, {i % Width})x{Counts[i]}@{Done[i]}"));
    }

    public const long DefaultHorizonMs = 600_000;

    public SimTime? EstimatePickup(ElevatorSnapshot snapshot, Request request, SimTime now) => Project(snapshot, request, now).PickupAt;

    public Outcome Project(ElevatorSnapshot snapshot, Request? extra, SimTime now)
    {
        var start = snapshot.At > now ? snapshot.At : now;
        var run = new Run(snapshot, scheduling, timing, extra);
        run.AdvanceTo(start.Ms, fastForward: false);
        if (extra is not null) run.Insert(extra);
        var horizon = start.AddMs(horizonMs);
        run.AdvanceTo(horizon.Ms, fastForward: true);
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
        for (var i = 0; i < without.Counts.Length; i++)
        {
            var wCount = without.Counts[i];
            var wDone = without.Done[i];
            if (wCount == 0 || wDone < 0) continue;
            var xCount = i < with.Counts.Length ? with.Counts[i] : 0;
            var xDone = i < with.Done.Length && with.Done[i] >= 0 ? with.Done[i] : with.Horizon.Ms;
            var both = Math.Min(wCount, xCount);
            added += both * (xDone - wDone) + (wCount - both) * (with.Horizon.Ms - wDone);
        }
        return added;
    }

    private sealed class Pickup(int source, HallCall call)
    {
        public int Source { get; } = source;
        public int Floor { get; } = call.Floor;
        public Direction Direction { get; } = call.Direction;
        public SimTime RegisteredAt { get; } = call.RegisteredAt;
        public HallCall Call { get; } = call;
        public Request? Extra { get; set; }
        public bool HasRequests => Call.Requests.Count > 0 || Extra is not null;
        public HallCall PolicyView { get; } = new(call.Floor, call.Direction, call.RegisteredAt);
        public int MaxLoad => Extra is { } x ? Math.Max(Call.MaxLoad, x.Load) : Call.MaxLoad;
    }

    private sealed class Run
    {
        private readonly ISchedulingPolicy _policy;
        private readonly ElevatorTiming _t;
        private readonly ulong _served;
        private readonly int _floors, _capacity, _id;
        private readonly double _threshold;
        private bool _viewBypassing;
        private long _viewRoom = long.MinValue;
        private readonly Request? _extra;
        private readonly bool[] _carCalls;
        private readonly List<Pickup> _pickups = [];
        private readonly List<int>[] _aboardByDest;
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
        private ElevatorSnapshot? _view;
        private readonly List<int> _viewCarCalls = [];
        private readonly List<HallCall> _viewPickups = [];
        private readonly IFastSchedulingPolicy? _fast;
        private ulong _carMask, _upMask, _downMask;
        private PickupRef[] _pickupRefs = [];
        private int _visiblePickups;
        private bool _pickupsDirty = true;
        private int? _target;
        private bool _targetValid;

        public Outcome Result { get; }

        public Run(ElevatorSnapshot s, ISchedulingPolicy policy, ElevatorTiming timing, Request? extra)
        {
            Result = new Outcome(s.HallCalls.Count + 1, s.Floors);
            _policy = policy;
            _fast = s.Floors <= StopSet.MaxFloors ? policy as IFastSchedulingPolicy : null;
            _t = s.Timing ?? timing;
            _served = s.ServedFloors;
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
            foreach (var c in s.CarCalls) SetCarCall(c, true);
            for (var i = 0; i < s.HallCalls.Count; i++) _pickups.Add(new Pickup(i, s.HallCalls[i]));
            _nextSource = s.HallCalls.Count;

            _aboardByDest = new List<int>[_floors + 2];
            _loadByDest = new int[_floors + 2];
            var riders = s.RiderTotals;
            for (var d = 1; d <= _floors && d < riders.Span; d++)
            {
                if (riders.CountTo(d) == 0) continue;
                Board(source: -1, d, riders.CountTo(d), riders.LoadTo(d));
            }
        }

        private bool _fastForward;
        private long _until;

        public void AdvanceTo(long until, bool fastForward)
        {
            _fastForward = fastForward;
            _until = until;
            DecideFromIdle();
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
            switch (_state)
            {
                case ElevatorState.MovingUp or ElevatorState.MovingDown:
                    var direction = _state == ElevatorState.MovingUp ? Direction.Up : Direction.Down;
                    while (true)
                    {
                        _floor += direction == Direction.Up ? 1 : -1;
                        _level = true;
                        if (ShouldStopHere(direction))
                        {
                            OpenDoor();
                            return;
                        }
                        _level = false;
                        var next = _now + _t.FloorTravelMs;
                        if (!_fastForward || next > _until)
                        {
                            _deadline = next;
                            return;
                        }
                        _now = next;
                    }
                case ElevatorState.DoorOpening:
                    _state = ElevatorState.DoorOpen;
                    _dir ??= _pickups.FirstOrDefault(p => p.Floor == _floor)?.Direction;
                    Changed();
                    Alight();
                    AnswerHere();
                    _deadline = _now + _t.DoorDwellMs;
                    break;
                case ElevatorState.DoorOpen:
                    Changed();
                    _state = ElevatorState.DoorClosing;
                    _closingStartedAt = _now;
                    _deadline = _now + _t.DoorClosingMs;
                    break;
                case ElevatorState.DoorClosing:
                    Changed();
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
            if (!FloorMask.Contains(_served, _floor)) return false;
            if (!_targetValid)
            {
                _target = Decide().Target;
                _targetValid = true;
            }
            return _target is null || _target == _floor || !direction.IsAhead(_floor, _target.Value);
        }

        private bool Bypassing => _load >= _threshold * _capacity;

        private void Changed()
        {
            _view = null;
            _targetValid = false;
            _pickupsDirty = true;
        }

        private void SetCarCall(int floor, bool on)
        {
            _carCalls[floor] = on;
            if (floor <= StopSet.MaxFloors)
                _carMask = on ? _carMask | StopSet.Bit(floor) : _carMask & ~StopSet.Bit(floor);
        }

        private void DecideFromIdle()
        {
            if (_state != ElevatorState.Idle) return;
            var target = Decide().Target;
            if (target is null)
            {
                _dir = null;
                Changed();
            }
            else if (target == _floor) OpenDoor();
            else StartMoving(target > _floor ? Direction.Up : Direction.Down);
        }

        private void StartMoving(Direction d)
        {
            Changed();
            _state = d == Direction.Up ? ElevatorState.MovingUp : ElevatorState.MovingDown;
            _dir = d;
            _level = false;
            _deadline = _now + _t.FloorTravelMs;
        }

        private void OpenDoor()
        {
            Changed();
            if (_state == ElevatorState.DoorClosing)
            {
                _state = ElevatorState.DoorOpening;
                _deadline = _now + (_now - _closingStartedAt) * _t.DoorOpeningMs / _t.DoorClosingMs;
                return;
            }
            SetCarCall(_floor, false);
            Changed();
            var lantern = LanternForStop();
            _state = ElevatorState.DoorOpening;
            _level = true;
            _dir = lantern;
            Changed();
            _deadline = _now + _t.DoorOpeningMs;
        }

        private Direction? LanternForStop()
        {
            var decision = Decide();
            if (decision.Target is not { } target) return null;
            if (target != _floor) return Directions.Between(_floor, target);
            return decision.ServeDirection ?? _pickups.FirstOrDefault(p => p.Floor == _floor)?.Direction;
        }

        private void Alight()
        {
            var wasBypassing = Bypassing;
            _load -= _loadByDest[_floor];
            _loadByDest[_floor] = 0;
            if (wasBypassing != Bypassing) Changed();
            if (_aboardByDest[_floor] is { Count: > 0 } groups)
            {
                foreach (var slot in groups) Result.Done[slot] = _now;
                groups.Clear();
            }
            if (_state == ElevatorState.DoorOpen && AnswerHere()) _deadline = _now + _t.DoorDwellMs;
        }

        private bool AnswerHere()
        {
            if (Bypassing) return false;
            var index = _pickups.FindIndex(p => p.Floor == _floor && p.Direction == _dir);
            if (index < 0) return false;
            var stop = _pickups[index];
            _pickups.RemoveAt(index);
            Changed();
            if (!stop.HasRequests) return true;

            var totals = stop.Call.Totals;
            var extraLoad = stop.Extra?.Load ?? 0;
            if (_load + totals.TotalLoad + extraLoad <= _capacity)
            {
                for (var d = 1; d < totals.Span; d++)
                    if (totals.CountTo(d) > 0) Board(stop.Source, d, totals.CountTo(d), totals.LoadTo(d));
                if (stop.Extra is { } x) BoardExtra(x);
            }
            else
            {
                var blocked = false;
                foreach (var r in stop.Call.Requests)
                {
                    if (_load + r.Load > _capacity)
                    {
                        blocked = true;
                        break;
                    }
                    Board(stop.Source, r.DestinationFloor, 1, r.Load);
                }
                if (!blocked && stop.Extra is { } x && _load + x.Load <= _capacity) BoardExtra(x);
            }

            if (Bypassing) Changed();
            return true;
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
            var slot = Result.Slot(source, dest);
            if (Result.Counts[slot] == 0) (_aboardByDest[dest] ??= []).Add(slot);
            Result.Counts[slot] += count;
            Result.Done[slot] = -1;
            _load += load;
            _loadByDest[dest] += load;
            if (source >= 0) AddDropOff(dest);
        }

        /// <summary>Elevator.AddRequest: a drop-off for the floor it stands at is ignored.</summary>
        private void AddDropOff(int floor)
        {
            if (floor == _floor && _level) return;
            if (!_carCalls[floor]) Changed();
            SetCarCall(floor, true);
        }

        public void Insert(Request r)
        {
            var existing = _pickups.FirstOrDefault(p => p.Floor == r.PickupFloor && p.Direction == r.Direction);
            Changed();
            if (existing is not null)
            {
                existing.Extra = r;
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
            if (Bypassing) return;
            if (_load + r.Load > _capacity) return;
            _dir ??= r.Direction;
            Changed();
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

        private PolicyDecision Decide()
        {
            Result.Steps++;
            var bypassing = Bypassing;
            if (bypassing != _viewBypassing)
            {
                _viewBypassing = bypassing;
                _view = null;
                _pickupsDirty = true;
            }
            var room = (long)_capacity - _load;
            if (room != _viewRoom)
            {
                _viewRoom = room;
                _view = null;
                _pickupsDirty = true;
            }
            if (_fast is not null)
            {
                if (bypassing)
                    return _fast.Decide(new StopSet(_floor, _state, _dir, _carMask, 0, 0, []));
                if (_pickupsDirty)
                {
                    _upMask = _downMask = 0;
                    if (_pickupRefs.Length < _pickups.Count) _pickupRefs = new PickupRef[_floors * 2 + 2];
                    _visiblePickups = 0;
                    foreach (var p in _pickups)
                    {
                        if (p.MaxLoad > room) continue;
                        _pickupRefs[_visiblePickups++] = new PickupRef(p.Floor, p.Direction, p.RegisteredAt.Ms);
                        if (p.Direction == Direction.Up) _upMask |= StopSet.Bit(p.Floor);
                        else _downMask |= StopSet.Bit(p.Floor);
                    }
                    _pickupsDirty = false;
                }
                return _fast.Decide(new StopSet(_floor, _state, _dir, _carMask, _upMask, _downMask, _pickupRefs.AsSpan(0, _visiblePickups)));
            }

            if (_view is null)
            {
                _viewCarCalls.Clear();
                for (var f = 1; f <= _floors; f++) if (_carCalls[f]) _viewCarCalls.Add(f);
                _viewPickups.Clear();
                if (!bypassing) foreach (var p in _pickups) if (p.MaxLoad <= room) _viewPickups.Add(p.PolicyView);
                _view = ElevatorSnapshot.ForPolicy(_floor, _state, _dir, _viewCarCalls, _viewPickups, _floors, _id);
            }
            else if (_view.CurrentFloor != _floor)
            {
                _view = _view with { CurrentFloor = _floor };
            }
            return _policy.Decide(_view);
        }
    }
}
