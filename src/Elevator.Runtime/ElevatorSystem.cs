using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Metrics;
using Elevator.Policy;
using Elevator.Policy.Dispatch;
using Elevator.Simulation;

namespace Elevator.Runtime;

public sealed record ActorStats(ActorKind Kind, int Actors, long Messages, WallDuration InHandlers, WallDuration Delivering);

/// <summary>Every elevator's access policies refused the request (ADR-012); <see cref="ElevatorSystem.AssignRequest"/> throws it.</summary>
public sealed class RequestRefusedException(int requestId, string policy, string reason)
    : InvalidOperationException($"request {requestId} refused by access policy '{policy}': {reason}")
{
    public int RequestId { get; } = requestId;

    public string Policy { get; } = policy;
}

/// <summary>An accepted assignment, as seen by whoever submitted the request.</summary>
public sealed record Assignment(int RequestId, int ElevatorId, WallDuration Latency);

/// <summary>
/// The spec's ElevatorSystem, as a running, thread-safe system (ADR-005).
/// <list type="bullet">
/// <item><c>requestQueue</c> → <see cref="RequestQueueCapacity"/>: a bounded channel read by the single dispatcher;
/// submitters wait when it is full.</item>
/// <item><c>assignRequest()</c> → <see cref="AssignRequest"/>: submit and await the accepted assignment.</item>
/// <item><c>findBestElevator()</c> → the dispatch policy inside the dispatcher actor (<see cref="DispatchPolicy"/>).</item>
/// <item><c>balanceLoad()</c> → the cost function's load terms, plus optional rebalancing (ADR-006).</item>
/// </list>
/// Elevators, the dispatcher and the passengers are actors running the same pure handlers as the simulator.
/// This class only moves messages and time: it makes no decisions.
/// </summary>
public sealed class ElevatorSystem : IAsyncDisposable
{
    private readonly Dictionary<Address, Actor> _actors = [];
    private readonly Channel<SubmitRequest> _requests;
    private readonly Channel<DomainEvent> _log = Channel.CreateUnbounded<DomainEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, (TaskCompletionSource<Assignment> Tcs, long Started)> _pending = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<int?>> _queries = new();
    private int _nextQuery;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private readonly Task _logWriter;
    private readonly ConcurrentQueue<DomainEvent> _events = new();
    private readonly Action<long, DomainEvent>? _sink;
    private readonly RuntimeOptions _options;

    private ElevatorSystem(RuntimeOptions options, Action<long, DomainEvent>? sink)
    {
        _options = options;
        _sink = sink;
        Clock = new RealClock(options.Speed);
        _requests = Channel.CreateBounded<SubmitRequest>(new BoundedChannelOptions(options.RequestQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        var timing = options.EffectiveTiming;
        var scheduling = SchedulingPolicies.Create(options.Scheduling);
        var dispatch = DispatchPolicies.Create(options.Dispatch, scheduling, timing);
        DispatchPolicy = dispatch.Name;
        var latency = options.Latency ?? MessageLatency.None;
        var estimator = new StopProjection(scheduling, timing);

        var fleet = options.EffectiveFleet;
        if (fleet.Count != options.Elevators)
            throw new ArgumentException($"the fleet has {fleet.Count} elevators but the options say {options.Elevators}", nameof(options));
        for (var i = 0; i < options.Elevators; i++)
        {
            var elevator = Building.CreateElevator(i, options.Floors, fleet[i], scheduling, options.BypassThreshold, options.CapacityMode);
            Add(new Actor(new ElevatorHandler(elevator, new ElevatorHandlerOptions(SnapshotDelayMs: latency.SnapshotMs), estimator), this));
        }
        var rebalance = options.Rebalance ? new HysteresisRebalance(scheduling, timing) : null;
        Add(new Actor(new DispatcherHandler(dispatch, options.Elevators, timing,
            new DispatcherOptions(latency.DispatchMs, rebalance is null ? null : 5_000, options.OverloadThreshold) { Access = Building.CreateAccess(options.Access) },
            rebalance, new NearestDispatch()),
            this, _requests.Reader));
        Add(new Actor(new PassengersHandler([.. fleet.Select(t => t.Capacity)]), this));

        _logWriter = Task.Run(WriteLog);
        foreach (var actor in _actors.Values) _loops.Add(actor.Start(_stop.Token));
        for (var i = 0; i < options.Elevators; i++) _actors[Address.Elevator(i)].Mailbox.Writer.TryWrite(new Start());
    }

    public static ElevatorSystem Start(RuntimeOptions options, Action<long, DomainEvent>? sink = null) => new(options, sink);

    public RealClock Clock { get; }

    public string DispatchPolicy { get; }

    public int RequestQueueCapacity => _options.RequestQueueCapacity;

    /// <summary>Requests submitted and not yet assigned.</summary>
    public int PendingAssignments => _pending.Count;

    /// <summary>
    /// Submits <paramref name="request"/> and completes when an elevator has accepted it. Thread-safe; any number of
    /// callers. Waits (asynchronously) while the request queue is full. Throws <see cref="RequestRefusedException"/> when
    /// no elevator may serve it (access policies, ADR-012).
    /// </summary>
    public async Task<Assignment> AssignRequest(Request request, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var tcs = new TaskCompletionSource<Assignment>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, (tcs, started)))
            throw new InvalidOperationException($"request {request.Id} is already pending");

        // The passenger exists before the request does: every notice about it then reaches them afterwards.
        _actors[Address.Passengers].Mailbox.Writer.TryWrite(new PassengerWaiting(request));
        await _requests.Writer.WriteAsync(new SubmitRequest(request), ct).ConfigureAwait(false);
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
            return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Spec's <c>findBestElevator()</c>: the elevator the dispatcher would choose for <paramref name="request"/> right
    /// now, from its current snapshots, without assigning anything. Null when none is eligible. Answered by the
    /// dispatcher actor itself, so it never reads shared state.
    /// </summary>
    public async Task<int?> FindBestElevator(Request request, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextQuery);
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queries[id] = tcs;
        _actors[Address.Dispatcher].Mailbox.Writer.TryWrite(new FindBestQuery(id, request));
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
            return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Spec's <c>balanceLoad()</c>. Load balancing is built into the cost function (predicted load, ADR-006); this
    /// additionally asks the dispatcher for a rebalancing pass now, which moves requests only when rebalancing is
    /// enabled (<see cref="RuntimeOptions.Rebalance"/>).
    /// </summary>
    public void BalanceLoad() => _actors[Address.Dispatcher].Mailbox.Writer.TryWrite(new RebalanceNow());

    /// <summary>
    /// Delivers <paramref name="message"/> to the actor at <paramref name="to"/> (shared v3 contract: the injection seam
    /// for fault and operator commands, as <c>SimEngine.Post</c> in the simulator).
    /// </summary>
    public void Post(Address to, Message message) => _actors[to].Mailbox.Writer.TryWrite(message);

    /// <summary>Spec's <c>requestQueue</c>: submissions waiting in the bounded request channel.</summary>
    public int RequestQueueLength => _requests.Reader.Count;

    /// <summary>Feeds scripted/generated passengers at their arrival times (sim time on this system's clock).</summary>
    public Task PlayPassengers(IEnumerable<Passenger> passengers, CancellationToken ct = default) => Task.Run(async () =>
    {
        foreach (var p in passengers.OrderBy(p => p.ArrivesAt).ThenBy(p => p.Id))
        {
            var wait = Clock.WallUntil(p.ArrivesAt);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _actors[Address.Passengers].Mailbox.Writer.TryWrite(new PassengerArrives(p.ToRequest()));
        }
    }, ct);

    /// <summary>Events recorded so far, in log order (only with <see cref="RuntimeOptions.KeepEvents"/>).</summary>
    public IReadOnlyList<DomainEvent> Events => [.. _events];

    /// <summary>The first exception that killed an actor loop, if any (a handler bug).</summary>
    public Exception? Fault => _loops.FirstOrDefault(t => t.IsFaulted)?.Exception?.InnerException;

    public PassengersHandler PassengersForInspection => (PassengersHandler)_actors[Address.Passengers].Handler;

    /// <summary>Per actor kind: messages handled, time in handlers, time delivering. Read after the system has stopped.</summary>
    public IReadOnlyList<ActorStats> Stats() =>
        _actors.Values.GroupBy(a => a.Handler.Address.Kind)
            .Select(g => new ActorStats(g.Key, g.Count(), g.Sum(a => a.MessagesHandled), new WallDuration(g.Sum(a => a.HandlerTicks)),
                new WallDuration(g.Sum(a => a.DeliveryTicks))))
            .ToList();

    internal void Record(DomainEvent e) => _log.Writer.TryWrite(e);

    internal async ValueTask Deliver(Envelope envelope, CancellationToken ct)
    {
        if (envelope.DelayMs > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(Clock.WallFor(envelope.DelayMs), ct).ConfigureAwait(false);
                await Deliver(envelope with { DelayMs = 0 }, ct).ConfigureAwait(false);
            }, ct);
            return;
        }

        switch (envelope)
        {
            case { To.Kind: ActorKind.Client, Message: RequestAssigned assigned }:
                if (_pending.TryRemove(assigned.RequestId, out var p))
                    p.Tcs.TrySetResult(new Assignment(assigned.RequestId, assigned.ElevatorId, WallDuration.Since(p.Started)));
                break;
            case { To.Kind: ActorKind.Client, Message: RequestRefused refused }:
                if (_pending.TryRemove(refused.RequestId, out var r))
                    r.Tcs.TrySetException(new RequestRefusedException(refused.RequestId, refused.Policy, refused.Reason));
                break;
            case { To.Kind: ActorKind.Client, Message: BestElevatorAnswer answer }:
                if (_queries.TryRemove(answer.QueryId, out var query)) query.TrySetResult(answer.ElevatorId);
                break;
            case { To.Kind: ActorKind.Client }:
                break;
            case { To.Kind: ActorKind.Dispatcher, Message: SubmitRequest submit }:
                await _requests.Writer.WriteAsync(submit, ct).ConfigureAwait(false); // passengers' submissions share the bounded queue
                break;
            default:
                _actors[envelope.To].Mailbox.Writer.TryWrite(envelope.Message);
                break;
        }
    }

    private void Add(Actor actor) => _actors.Add(actor.Handler.Address, actor);

    private async Task WriteLog()
    {
        long seq = 0;
        await foreach (var e in _log.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _sink?.Invoke(seq++, e);
            if (_options.KeepEvents) _events.Enqueue(e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        _log.Writer.TryComplete();
        await _logWriter.ConfigureAwait(false);
        foreach (var (_, p) in _pending) p.Tcs.TrySetCanceled();
        _stop.Dispose();
    }
}
