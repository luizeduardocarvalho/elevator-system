using Elevator.Domain.Events;

namespace Elevator.Domain.Control;

/// <param name="DispatchDelayMs">Delay of an Assign message reaching the elevator (dispatcher cycle / network).</param>
/// <param name="OverloadThreshold">Requests in flight above which the dispatcher switches to its cheap fallback policy
/// (defense in depth, ADR-006); null = never.</param>
/// <param name="EmitDispatchEvaluations">Log every candidate's cost before each proposal (shared v3 contract; the TUI's
/// ASSIGN line). Off by default, so normal logs are unchanged.</param>
public sealed record DispatcherOptions(long DispatchDelayMs = 0, int? OverloadThreshold = null, bool EmitDispatchEvaluations = false)
{
    public static DispatcherOptions Default { get; } = new();

    /// <summary>Access policies applied before the dispatch policy (ADR-012). Default: allow everything.</summary>
    public AccessPipeline Access { get; init; } = AccessPipeline.AllowAll;

    /// <summary>
    /// Watchdog (ADR-013): an elevator with work that shows no progress for this long (sim ms) is declared faulty.
    /// Null = no watchdog. The shells set 30 s by default.
    /// </summary>
    public long? WatchdogTimeoutMs { get; init; }

    /// <summary>VIP priority (ADR-015): passed to the dispatch policy as a cost term; it never reorders requests.</summary>
    public bool VipPriority { get; init; }
}

/// <summary>
/// The single dispatcher: owns every in-flight request and assigns each to exactly one elevator.
///
/// Lifecycle of a request: Pending → Proposed(e) → Assigned(e) → done when picked up.
/// <list type="bullet">
/// <item>Submissions are idempotent by request id while the request is in flight.</item>
/// <item>A reject excludes that elevator for this request until its next snapshot, then the request is
/// re-dispatched. If every eligible elevator rejected it as stale, the next proposal is forced.</item>
/// <item>A release (maintenance, emergency, fault) or a re-registration (left behind) puts it back to Pending,
/// keeping its id and t_call.</item>
/// <item>Pending requests are retried oldest-first, by (t_call, id), whenever an elevator publishes a new snapshot
/// (aging order). Nothing reorders them: urgency (VIP) is a cost term and eligibility, not a queue position (ADR-015).</item>
/// <item>Eligibility comes before cost (ADR-012): the access pipeline is evaluated once per request (its answer depends
/// only on the elevators' configuration); a denied elevator is never a candidate, and if every elevator is denied the
/// request is refused (AccessDenied with no elevator) instead of waiting forever. Elevators out of service (any mode
/// but Normal: draining, maintenance, emergency, fault; ADR-013) are not candidates either.</item>
/// </list>
/// The dispatcher sees elevators only through snapshots, which may be stale; the elevator has the final say.
/// </summary>
public sealed class DispatcherHandler(
    IDispatchPolicy policy,
    int elevatorCount,
    ElevatorTiming timing,
    DispatcherOptions? options = null,
    IDispatchPolicy? overloadFallback = null) : IHandler
{
    private enum Status { Pending, Proposed, Assigned }

    private sealed class Tracked(Request request)
    {
        public Request Request { get; } = request;
        public Status Status { get; set; } = Status.Pending;
        public (long, int) Key => (Request.Timestamp.Ms, Request.Id);
        public IReadOnlyDictionary<int, AccessDenial>? Denied { get; set; }
        public int? Elevator { get; set; }
        public HashSet<int> Excluded { get; } = [];
        public bool OnlyStaleRejections { get; set; } = true;
        public bool Deferred { get; set; }
    }

    private readonly DispatcherOptions _options = options ?? DispatcherOptions.Default;
    private readonly Dictionary<int, Tracked> _requests = [];
    private readonly SortedDictionary<int, ElevatorSnapshot> _snapshots = [];
    private static readonly IReadOnlyDictionary<int, AccessDenial> NoDenials = new Dictionary<int, AccessDenial>();
    private readonly SortedSet<(long CalledAtMs, int Id)> _pending = [];
    private readonly Dictionary<int, HashSet<int>> _excludedFrom = [];
    private readonly Dictionary<int, List<(Request Request, long AcceptedVersion)>> _overlay = [];
    private readonly Dictionary<int, ElevatorSnapshot> _overlaid = [];
    private sealed class Watch
    {
        public (int Floor, ElevatorState State, SimTime? PhaseEndsAt) Progress;
        public SimTime Since;
        public bool Watching;
        public long? DeclaredAtVersion;
    }

    private readonly Dictionary<int, Watch> _watch = [];
    private bool _watchdogScheduled;
    private bool _overloaded;

    public Address Address => Address.Dispatcher;

    public SimTime? NextWakeUp => null;

    public IDispatchPolicy Policy => policy;

    /// <summary>Requests not yet picked up.</summary>
    public int InFlight => _requests.Count;

    public void Handle(Message message, SimTime now, Outbox outbox)
    {
        switch (message)
        {
            case SubmitRequest submit:
                OnSubmit(submit.Request, now, outbox);
                break;
            case SnapshotPublished published when _snapshots.TryGetValue(published.Snapshot.ElevatorId, out var current)
                                                  && published.Snapshot.Version < current.Version:
                break;
            case SnapshotPublished published:
                _snapshots[published.Snapshot.ElevatorId] = published.Snapshot;
                if (_overlay.TryGetValue(published.Snapshot.ElevatorId, out var shown))
                    shown.RemoveAll(o => o.AcceptedVersion <= published.Snapshot.Version);
                _overlaid.Remove(published.Snapshot.ElevatorId);
                if (_excludedFrom.Remove(published.Snapshot.ElevatorId, out var excluded))
                    foreach (var id in excluded) Find(id)?.Excluded.Remove(published.Snapshot.ElevatorId);
                Observe(published.Snapshot, now, outbox);
                RetryPending(now, outbox);
                break;
            case WatchdogTimer:
                _watchdogScheduled = false;
                CheckWatchdog(now, outbox);
                break;
            case ProgressReported progress when _watch.TryGetValue(progress.ElevatorId, out var watched) && watched.Watching:
                if (progress.At > watched.Since) watched.Since = progress.At;
                break;
            case AssignmentAcceptedMsg accepted when Find(accepted.RequestId) is { } t:
                if (_overlay.TryGetValue(accepted.ElevatorId, out var proposed))
                {
                    var i = proposed.FindIndex(o => o.Request.Id == accepted.RequestId);
                    if (i >= 0) proposed[i] = (proposed[i].Request, accepted.Version);
                }
                SetStatus(t, Status.Assigned);
                t.Elevator = accepted.ElevatorId;
                t.Deferred = false;
                outbox.Send(Address.Client, new RequestAssigned(accepted.RequestId, accepted.ElevatorId));
                break;
            case AssignmentRejectedMsg rejected when Find(rejected.RequestId) is { } t:
                if (_overlay.TryGetValue(rejected.ElevatorId, out var bounced)) bounced.RemoveAll(o => o.Request.Id == rejected.RequestId);
                _overlaid.Remove(rejected.ElevatorId);
                SetStatus(t, Status.Pending);
                t.Elevator = null;
                t.Excluded.Add(rejected.ElevatorId);
                (_excludedFrom.TryGetValue(rejected.ElevatorId, out var set) ? set : _excludedFrom[rejected.ElevatorId] = []).Add(t.Request.Id);
                if (rejected.Reason != AssignmentReason.Stale) t.OnlyStaleRejections = false;
                TryDispatch(t, now, outbox);
                break;
            case AssignmentReleasedMsg released when Find(released.RequestId) is { Status: Status.Assigned } t && t.Elevator == released.ElevatorId:
                BackToPending(t);
                TryDispatch(t, now, outbox);
                break;
            case ReRegister again when Find(again.RequestId) is { } t:
                BackToPending(t);
                TryDispatch(t, now, outbox);
                break;
            case RequestPickedUp picked:
                if (_requests.Remove(picked.RequestId, out var done)) _pending.Remove(done.Key);
                break;
            case FindBestQuery query:
                var best = _snapshots.Count < elevatorCount
                    ? null
                    : policy.Choose(new DispatchContext(query.Request,
                        [.. _snapshots.Values.Where(s => InService(s) && _options.Access.Check(query.Request, s) is null).Select(WithOverlay)],
                        now, timing) { VipPriority = _options.VipPriority });
                outbox.Send(Address.Client, new BestElevatorAnswer(query.QueryId, best?.ElevatorId, best?.Cost ?? double.NaN));
                break;
        }
    }

    private Tracked? Find(int requestId) => _requests.GetValueOrDefault(requestId);

    private void OnSubmit(Request request, SimTime now, Outbox outbox)
    {
        if (_requests.ContainsKey(request.Id))
        {
            outbox.Emit(new DuplicateSubmission(now, request.Id));
            return;
        }

        var t = new Tracked(request);
        _requests.Add(request.Id, t);
        _pending.Add(t.Key);
        outbox.Emit(new RequestSubmitted(now, request.Id, request.PickupFloor, request.DestinationFloor));
        TryDispatch(t, now, outbox);
    }

    private void SetStatus(Tracked t, Status status)
    {
        t.Status = status;
        if (status == Status.Pending) _pending.Add(t.Key);
        else _pending.Remove(t.Key);
    }

    private void BackToPending(Tracked t)
    {
        SetStatus(t, Status.Pending);
        t.Elevator = null;
        t.Excluded.Clear();
        t.OnlyStaleRejections = true;
    }

    private void RetryPending(SimTime now, Outbox outbox)
    {
        foreach (var (_, id) in _pending.ToList())
            if (Find(id) is { Status: Status.Pending } t) TryDispatch(t, now, outbox);
    }

    private void TryDispatch(Tracked t, SimTime now, Outbox outbox)
    {
        if (_snapshots.Count < elevatorCount) return;
        if (t.Denied is null && !CheckAccess(t, now, outbox)) return;

        var overloaded = overloadFallback is not null && _options.OverloadThreshold is { } limit && _requests.Count > limit;
        if (overloaded != _overloaded)
        {
            _overloaded = overloaded;
            outbox.Emit(new OverloadModeChanged(now, overloaded, _requests.Count));
        }
        var active = overloaded ? overloadFallback! : policy;

        Func<ElevatorSnapshot, ElevatorSnapshot> view = overloaded ? (s => s) : WithOverlay;
        var candidates = _snapshots.Values.Where(s => !t.Excluded.Contains(s.ElevatorId) && Available(t, s)).Select(view).ToList();
        var explain = _options.EmitDispatchEvaluations;
        var choice = active.Choose(new DispatchContext(t.Request, candidates, now, timing) { Explain = explain, VipPriority = _options.VipPriority });
        var forced = false;

        if (choice is null && t.Excluded.Count > 0 && t.OnlyStaleRejections)
        {
            choice = active.Choose(new DispatchContext(t.Request, [.. _snapshots.Values.Where(s => Available(t, s)).Select(view)], now, timing)
                { Explain = explain, VipPriority = _options.VipPriority });
            forced = choice is not null;
        }

        if (explain)
            outbox.Emit(new DispatchEvaluated(now, t.Request.Id,
                WithUnavailable(t, choice?.Candidates ?? [.. candidates.OrderBy(s => s.ElevatorId).Select(s => new CandidateCost(s.ElevatorId, null, "ineligible"))]),
                choice?.ElevatorId));

        if (choice is null)
        {
            if (!t.Deferred) outbox.Emit(new DispatchDeferred(now, t.Request.Id));
            t.Deferred = true;
            return;
        }

        SetStatus(t, Status.Proposed);
        t.Elevator = choice.ElevatorId;
        if (!overloaded)
        {
            (_overlay.TryGetValue(choice.ElevatorId, out var list) ? list : _overlay[choice.ElevatorId] = []).Add((t.Request, long.MaxValue));
            if (_overlaid.TryGetValue(choice.ElevatorId, out var current)) _overlaid[choice.ElevatorId] = Merge(current, t.Request);
        }
        outbox.Emit(new AssignmentProposed(now, t.Request.Id, choice.ElevatorId, Math.Round(choice.Cost, 3), choice.Eta?.Ms, forced));
        outbox.Send(Address.Elevator(choice.ElevatorId), new Assign(t.Request, choice.Eta, forced), _options.DispatchDelayMs);
    }

    private bool InService(ElevatorSnapshot s) =>
        s.Mode == ElevatorMode.Normal && !(_watch.Count > 0 && _watch.TryGetValue(s.ElevatorId, out var w) && w.DeclaredAtVersion is not null);

    private bool Available(Tracked t, ElevatorSnapshot s) => InService(s) && !t.Denied!.ContainsKey(s.ElevatorId);

    private void Observe(ElevatorSnapshot s, SimTime now, Outbox outbox)
    {
        if (_options.WatchdogTimeoutMs is not { } limit) return;
        if (!_watch.TryGetValue(s.ElevatorId, out var w)) _watch[s.ElevatorId] = w = new Watch();

        if (w.DeclaredAtVersion is { } declared && s.Version > declared) w.DeclaredAtVersion = null;
        var progress = (s.CurrentFloor, s.State, s.PhaseEndsAt);
        var hasWork = s.Mode is ElevatorMode.Normal or ElevatorMode.Draining
                      && (s.State is not (ElevatorState.Idle or ElevatorState.Maintenance) || s.CarCalls.Count > 0 || s.HallCalls.Count > 0);
        if (!hasWork || w.DeclaredAtVersion is not null)
        {
            w.Watching = false;
            w.Progress = progress;
            return;
        }
        if (!w.Watching || progress != w.Progress)
        {
            w.Progress = progress;
            w.Since = s.At;
        }
        w.Watching = true;
        ScheduleWatchdog(w.Since.AddMs(limit), now, outbox);
    }

    private void ScheduleWatchdog(SimTime due, SimTime now, Outbox outbox)
    {
        if (_watchdogScheduled) return;
        _watchdogScheduled = true;
        outbox.Send(Address.Dispatcher, new WatchdogTimer(), Math.Max(0, due.Ms - now.Ms));
    }

    private void CheckWatchdog(SimTime now, Outbox outbox)
    {
        if (_options.WatchdogTimeoutMs is not { } limit) return;
        SimTime? next = null;
        foreach (var (id, w) in _watch.OrderBy(kv => kv.Key))
        {
            if (!w.Watching) continue;
            var due = w.Since.AddMs(limit);
            if (due > now)
            {
                next = next is { } n && n <= due ? n : due;
                continue;
            }
            w.Watching = false;
            w.DeclaredAtVersion = _snapshots[id].Version;
            outbox.Send(Address.Elevator(id), new FaultDeclared(now.Ms - w.Since.Ms));
        }
        if (next is { } at) ScheduleWatchdog(at, now, outbox);
    }

    private bool CheckAccess(Tracked t, SimTime now, Outbox outbox)
    {
        var pipeline = _options.Access;
        Dictionary<int, AccessDenial>? denied = null;
        if (pipeline.Policies.Count > 0)
            foreach (var s in _snapshots.Values)
                if (pipeline.Check(t.Request, s) is { } denial)
                {
                    (denied ??= [])[s.ElevatorId] = denial;
                    outbox.Emit(new AccessDenied(now, t.Request.Id, s.ElevatorId, denial.Policy, denial.Reason));
                }
        t.Denied = denied ?? NoDenials;
        if (denied is null || denied.Count < _snapshots.Count) return true;

        var policies = denied.Values.Select(d => d.Policy).Distinct().ToList();
        var (name, reason) = policies.Count == 1 ? (policies[0], denied.Values.First().Reason) : (string.Join("+", policies), "no elevator may serve it");
        outbox.Emit(new AccessDenied(now, t.Request.Id, null, name, reason));
        _requests.Remove(t.Request.Id);
        _pending.Remove(t.Key);
        outbox.Send(Address.Passengers, new RequestRefused(t.Request.Id, name, reason));
        outbox.Send(Address.Client, new RequestRefused(t.Request.Id, name, reason));
        return false;
    }

    private IReadOnlyList<CandidateCost> WithUnavailable(Tracked t, IReadOnlyList<CandidateCost> evaluated)
    {
        var unavailable = _snapshots.Values.Where(s => !Available(t, s)).ToList();
        if (unavailable.Count == 0) return evaluated;
        return
        [
            .. evaluated.Concat(unavailable.Select(s => new CandidateCost(s.ElevatorId, null,
                t.Denied!.TryGetValue(s.ElevatorId, out var d) ? d.Policy : s.Mode.ToString().ToLowerInvariant()))).OrderBy(c => c.ElevatorId),
        ];
    }

    private ElevatorSnapshot WithOverlay(ElevatorSnapshot s)
    {
        if (!_overlay.TryGetValue(s.ElevatorId, out var pending) || pending.Count == 0) return s;
        if (_overlaid.TryGetValue(s.ElevatorId, out var cached)) return cached;
        var overlaid = s;
        foreach (var (r, _) in pending) overlaid = Merge(overlaid, r);
        return _overlaid[s.ElevatorId] = overlaid;
    }

    private static ElevatorSnapshot Merge(ElevatorSnapshot s, Request r)
    {
        var calls = s.HallCalls as System.Collections.Immutable.ImmutableList<HallCall> ?? [.. s.HallCalls];
        var i = calls.FindIndex(c => c.Floor == r.PickupFloor && c.Direction == r.Direction);
        if (i >= 0)
        {
            var c = calls[i];
            var requests = c.Requests as System.Collections.Immutable.ImmutableList<Request> ?? [.. c.Requests];
            return s with { HallCalls = calls.SetItem(i, c with { Requests = requests.Add(r), Totals = c.Totals.Add(r) }) };
        }
        return s with
        {
            HallCalls = calls.Add(new HallCall(r.PickupFloor, r.Direction, s.At, System.Collections.Immutable.ImmutableList.Create(r))
            {
                Totals = DestinationTotals.Empty.Add(r),
            }),
        };
    }
}
