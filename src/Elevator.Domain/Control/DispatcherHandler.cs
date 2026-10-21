using Elevator.Domain.Events;

namespace Elevator.Domain.Control;

/// <param name="DispatchDelayMs">Delay of an Assign message reaching the elevator (dispatcher cycle / network).</param>
/// <param name="RebalanceIntervalMs">Optional rebalancing period; null = sticky assignments only (ADR-006).</param>
public sealed record DispatcherOptions(long DispatchDelayMs = 0, long? RebalanceIntervalMs = null)
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
    IRebalancePolicy? rebalance = null) : IHandler
{
    private enum Status { Pending, Proposed, Assigned }

    private sealed class Tracked(Request request)
    {
        public Request Request { get; } = request;
        public Status Status { get; set; } = Status.Pending;
        public int? Elevator { get; set; }
        public HashSet<int> Excluded { get; } = [];
        public bool OnlyStaleRejections { get; set; } = true;
        public bool Deferred { get; set; }
        public int Reassignments { get; set; }
    }

    private readonly DispatcherOptions _options = options ?? DispatcherOptions.Default;
    private readonly Dictionary<int, Tracked> _requests = [];
    private readonly SortedDictionary<int, ElevatorSnapshot> _snapshots = [];
    private long _sequence;
    private bool _rebalanceScheduled;

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
                foreach (var t in _requests.Values) t.Excluded.Remove(published.Snapshot.ElevatorId);
                RetryPending(now, outbox);
                ScheduleRebalance(outbox);
                break;
            case AssignmentAcceptedMsg accepted when Find(accepted.RequestId) is { } t:
                t.Status = Status.Assigned;
                t.Elevator = accepted.ElevatorId;
                t.Deferred = false;
                outbox.Send(Address.Client, new RequestAssigned(accepted.RequestId, accepted.ElevatorId));
                break;
            case AssignmentRejectedMsg rejected when Find(rejected.RequestId) is { } t:
                t.Status = Status.Pending;
                t.Elevator = null;
                t.Excluded.Add(rejected.ElevatorId);
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
                _requests.Remove(picked.RequestId);
                break;
            case RebalanceTimer:
                _rebalanceScheduled = false;
                Rebalance(now, outbox);
                ScheduleRebalance(outbox);
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
        outbox.Emit(new RequestSubmitted(now, request.Id, request.PickupFloor, request.DestinationFloor));
        TryDispatch(t, now, outbox);
    }

    private static void BackToPending(Tracked t)
    {
        t.Status = Status.Pending;
        t.Elevator = null;
        t.Excluded.Clear();
        t.OnlyStaleRejections = true;
    }

    private void RetryPending(SimTime now, Outbox outbox)
    {
        var pending = _requests.Values
            .Where(t => t.Status == Status.Pending)
            .OrderBy(t => t.Request.Timestamp).ThenBy(t => t.Request.Id) // oldest first: aging order
            .ToList();
        foreach (var t in pending) TryDispatch(t, now, outbox);
    }

    private void TryDispatch(Tracked t, SimTime now, Outbox outbox)
    {
        if (_snapshots.Count < elevatorCount) return; // not every elevator has reported yet; retried on next snapshot

        var candidates = _snapshots.Values.Where(s => !t.Excluded.Contains(s.ElevatorId)).ToList();
        var choice = policy.Choose(new DispatchContext(t.Request, candidates, now, timing, _sequence));
        var forced = false;

        if (choice is null && t.Excluded.Count > 0 && t.OnlyStaleRejections)
        {
            // Everyone eligible said "stale": stop bouncing and force the best one (ADR-006).
            choice = policy.Choose(new DispatchContext(t.Request, [.. _snapshots.Values], now, timing, _sequence));
            forced = choice is not null;
        }

        if (choice is null)
        {
            if (!t.Deferred) outbox.Emit(new DispatchDeferred(now, t.Request.Id));
            t.Deferred = true;
            return;
        }

        _sequence++;
        t.Status = Status.Proposed;
        t.Elevator = choice.ElevatorId;
        outbox.Emit(new AssignmentProposed(now, t.Request.Id, choice.ElevatorId, Math.Round(choice.Cost, 3), choice.Eta?.Ms, forced));
        outbox.Send(Address.Elevator(choice.ElevatorId), new Assign(t.Request, choice.Eta, forced), _options.DispatchDelayMs);
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
