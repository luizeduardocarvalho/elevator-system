using Elevator.Domain.Events;

namespace Elevator.Domain.Control;

/// <param name="DispatchDelayMs">Delay of an Assign message reaching the elevator (dispatcher cycle / network).</param>
/// <param name="RebalanceIntervalMs">Optional rebalancing period; null = sticky assignments only (ADR-006).</param>
/// <param name="OverloadThreshold">Requests in flight above which the dispatcher switches to its cheap fallback policy
/// (defense in depth, ADR-006); null = never.</param>
public sealed record DispatcherOptions(long DispatchDelayMs = 0, long? RebalanceIntervalMs = null, int? OverloadThreshold = null)
{
    public static DispatcherOptions Default { get; } = new();
}

/// <summary>Decides, for an assigned request, whether moving it elsewhere is worth it (optional, ADR-006).</summary>
public interface IRebalancePolicy
{
    /// <summary>Elevator to move the request to, or null to keep it where it is.</summary>
    int? BetterElevator(Request request, int currentElevator, int reassignmentsSoFar, IReadOnlyList<ElevatorSnapshot> snapshots, SimTime now);
}

/// <summary>
/// The single dispatcher: owns every in-flight request and assigns each to exactly one elevator.
///
/// Lifecycle of a request: Pending → Proposed(e) → Assigned(e) → done when picked up.
/// <list type="bullet">
/// <item>Submissions are idempotent by request id while the request is in flight.</item>
/// <item>A reject excludes that elevator for this request until its next snapshot, then the request is
/// re-dispatched. If every eligible elevator rejected it as stale, the next proposal is forced.</item>
/// <item>A release (full-load bypass, rebalance) or a re-registration (left behind) puts it back to Pending,
/// keeping its id and t_call.</item>
/// <item>Pending requests are retried oldest-first whenever an elevator publishes a new snapshot (aging order).</item>
/// </list>
/// The dispatcher sees elevators only through snapshots, which may be stale; the elevator has the final say.
/// </summary>
public sealed class DispatcherHandler(
    IDispatchPolicy policy,
    int elevatorCount,
    ElevatorTiming timing,
    DispatcherOptions? options = null,
    IRebalancePolicy? rebalance = null,
    IDispatchPolicy? overloadFallback = null) : IHandler
{
    private enum Status { Pending, Proposed, Assigned }

    private sealed class Tracked(Request request)
    {
        public Request Request { get; } = request;
        public Status Status { get; set; } = Status.Pending;
        public (long, int) Key => (Request.Timestamp.Ms, Request.Id);
        public int? Elevator { get; set; }
        public HashSet<int> Excluded { get; } = [];
        public bool OnlyStaleRejections { get; set; } = true;
        public bool Deferred { get; set; }
        public int Reassignments { get; set; }
    }

    private readonly DispatcherOptions _options = options ?? DispatcherOptions.Default;
    private readonly Dictionary<int, Tracked> _requests = [];
    private readonly SortedDictionary<int, ElevatorSnapshot> _snapshots = [];
    // Indexes so that no message costs O(requests in flight): pending requests oldest-first (aging order), and which
    // requests each elevator is currently excluded from.
    private readonly SortedSet<(long CalledAtMs, int Id)> _pending = [];
    private readonly Dictionary<int, HashSet<int>> _excludedFrom = [];
    // Pending-assignment overlay: what this dispatcher has proposed to each elevator that the elevator's latest
    // snapshot does not show yet. Without it, a burst of requests is all evaluated against the same snapshot, the
    // elevator (which does see the earlier ones) rejects as stale, and requests bounce. AcceptedVersion stays
    // long.MaxValue until the elevator accepts; the entry goes once a snapshot at or after that version arrives.
    private readonly Dictionary<int, List<(Request Request, long AcceptedVersion)>> _overlay = [];
    // The overlaid snapshot per elevator, kept between decisions (one merge per proposal) so that it is not rebuilt
    // per decision and the dispatch policy's per-snapshot caches keep working. Dropped when the base changes.
    private readonly Dictionary<int, ElevatorSnapshot> _overlaid = [];
    private long _sequence;
    private bool _rebalanceScheduled;
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
            case SnapshotPublished published:
                _snapshots[published.Snapshot.ElevatorId] = published.Snapshot;
                if (_overlay.TryGetValue(published.Snapshot.ElevatorId, out var shown))
                    shown.RemoveAll(o => o.AcceptedVersion <= published.Snapshot.Version);
                _overlaid.Remove(published.Snapshot.ElevatorId);
                if (_excludedFrom.Remove(published.Snapshot.ElevatorId, out var excluded))
                    foreach (var id in excluded) Find(id)?.Excluded.Remove(published.Snapshot.ElevatorId);
                RetryPending(now, outbox);
                ScheduleRebalance(outbox);
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
            case RebalanceTimer:
                _rebalanceScheduled = false;
                Rebalance(now, outbox);
                ScheduleRebalance(outbox);
                break;
            case RebalanceNow:
                Rebalance(now, outbox);
                break;
            case FindBestQuery query:
                var best = _snapshots.Count < elevatorCount
                    ? null
                    : policy.Choose(new DispatchContext(query.Request, [.. _snapshots.Values.Select(WithOverlay)], now, timing, _sequence));
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
        foreach (var (_, id) in _pending.ToList()) // oldest first: aging order
            if (Find(id) is { Status: Status.Pending } t) TryDispatch(t, now, outbox);
    }

    private void TryDispatch(Tracked t, SimTime now, Outbox outbox)
    {
        if (_snapshots.Count < elevatorCount) return; // not every elevator has reported yet; retried on next snapshot

        // Overload mode: above the threshold of requests in flight, use the cheap fallback so that decision time stays
        // bounded whatever the burst. Logged on every switch. The threshold sits far above any load in the bench
        // profiles, so normal results are unaffected.
        var overloaded = overloadFallback is not null && _options.OverloadThreshold is { } limit && _requests.Count > limit;
        if (overloaded != _overloaded)
        {
            _overloaded = overloaded;
            outbox.Emit(new OverloadModeChanged(now, overloaded, _requests.Count));
        }
        var active = overloaded ? overloadFallback! : policy;

        // The overlay serves the precise policy. In overload mode the fallback is meant to be cheap, and keeping the
        // overlay up to date would cost O(proposals ahead of the elevators) per snapshot, so it is skipped.
        Func<ElevatorSnapshot, ElevatorSnapshot> view = overloaded ? (s => s) : WithOverlay;
        var candidates = _snapshots.Values.Where(s => !t.Excluded.Contains(s.ElevatorId)).Select(view).ToList();
        var choice = active.Choose(new DispatchContext(t.Request, candidates, now, timing, _sequence));
        var forced = false;

        if (choice is null && t.Excluded.Count > 0 && t.OnlyStaleRejections)
        {
            // Everyone eligible said "stale": stop bouncing and force the best one (ADR-006).
            choice = active.Choose(new DispatchContext(t.Request, [.. _snapshots.Values.Select(view)], now, timing, _sequence));
            forced = choice is not null;
        }

        if (choice is null)
        {
            if (!t.Deferred) outbox.Emit(new DispatchDeferred(now, t.Request.Id));
            t.Deferred = true;
            return;
        }

        _sequence++;
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

    /// <summary>The elevator's snapshot plus the pickups this dispatcher has sent it that the snapshot does not show yet.</summary>
    private ElevatorSnapshot WithOverlay(ElevatorSnapshot s)
    {
        if (!_overlay.TryGetValue(s.ElevatorId, out var pending) || pending.Count == 0) return s;
        if (_overlaid.TryGetValue(s.ElevatorId, out var cached)) return cached;
        var overlaid = s;
        foreach (var (r, _) in pending) overlaid = Merge(overlaid, r);
        return _overlaid[s.ElevatorId] = overlaid;
    }

    /// <summary>Adds one pickup to a snapshot, as Elevator.AssignPickup would (joins its floor-and-direction stop).</summary>
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

    private void ScheduleRebalance(Outbox outbox)
    {
        if (rebalance is null || _options.RebalanceIntervalMs is not { } interval || _rebalanceScheduled || _requests.Count == 0) return;
        _rebalanceScheduled = true;
        outbox.Send(Address.Dispatcher, new RebalanceTimer(), interval);
    }

    private void Rebalance(SimTime now, Outbox outbox)
    {
        if (rebalance is null) return;
        var snapshots = _snapshots.Values.ToList();
        foreach (var t in _requests.Values.Where(t => t.Status == Status.Assigned).OrderBy(t => t.Request.Timestamp).ThenBy(t => t.Request.Id).ToList())
        {
            if (rebalance.BetterElevator(t.Request, t.Elevator!.Value, t.Reassignments, snapshots, now) is not { } better) continue;
            t.Reassignments++;
            outbox.Send(Address.Elevator(t.Elevator.Value), new Unassign(t.Request.Id)); // it confirms with a Released(Rebalance)
        }
    }
}
