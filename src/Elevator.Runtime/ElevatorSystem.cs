using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
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

/// <summary>
/// An actor's handler threw, so the system stopped serving (ADR-017). Pending and later calls fail with it instead of
/// waiting forever; <see cref="Exception.InnerException"/> is the handler's exception, also in <see cref="ElevatorSystem.Fault"/>.
/// </summary>
public sealed class ElevatorSystemFaultedException(Exception fault)
    : InvalidOperationException($"the elevator system stopped after a handler failed: {fault.Message}", fault);

/// <summary>An accepted assignment, as seen by whoever submitted the request.</summary>
/// <param name="CompletedAt">Stopwatch timestamp when the acceptance reached the runtime (before any continuation runs),
/// so an open-loop generator can measure from its own intended send time (ADR-015).</param>
public sealed record Assignment(int RequestId, int ElevatorId, WallDuration Latency, long CompletedAt = 0);

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
    private Task _logWriter = Task.CompletedTask;
    private readonly ConcurrentQueue<DomainEvent> _events = new();
    private readonly Action<long, DomainEvent>? _sink;
    private readonly RuntimeOptions _options;
    private readonly DelayedDelivery _delayed;

    private ElevatorSystem(RuntimeOptions options, Action<long, DomainEvent>? sink, EngineHandover? handover = null)
    {
        _options = options;
        _sink = sink;
        Clock = new RealClock(options.Speed, handover?.At ?? SimTime.Zero);
        _delayed = new DelayedDelivery(Clock, Deliver);
        _requests = Channel.CreateBounded<SubmitRequest>(new BoundedChannelOptions(options.RequestQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        if (handover is not null)
        {
            TakeOver(handover);
            return;
        }

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
            var handlerOptions = new ElevatorHandlerOptions(SnapshotDelayMs: latency.SnapshotMs, ReportProgress: options.WatchdogTimeoutMs is not null);
            Add(new Actor(new ElevatorHandler(elevator, handlerOptions, estimator), this));
        }
        var rebalance = options.Rebalance ? new HysteresisRebalance(scheduling, timing) : null;
        Add(new Actor(new DispatcherHandler(dispatch, options.Elevators, timing,
            new DispatcherOptions(latency.DispatchMs, rebalance is null ? null : 5_000, options.OverloadThreshold)
            {
                Access = Building.CreateAccess(options.Access),
                WatchdogTimeoutMs = options.WatchdogTimeoutMs,
                VipPriority = options.Access.VipPriority,
            },
            rebalance, new NearestDispatch()),
            this, _requests.Reader));
        Add(new Actor(new PassengersHandler([.. fleet.Select(t => t.Capacity)]), this));

        FaultInjector = new RuntimeFaultInjector(this);
        _logWriter = Task.Run(WriteLog);
        for (var i = 0; i < options.Elevators; i++) _actors[Address.Elevator(i)].Mailbox.Writer.TryWrite(new Start());
        if (!options.StartPaused) Resume();
    }

    /// <summary>
    /// The actor runtime taking over a simulation's handlers mid-run (ADR-015): the throughput ramp seeds the building
    /// with a busy moment of a real scenario (riders, accepted pickups, the dispatcher's view) instead of an empty one.
    /// Every message still in flight is delivered with its remaining delay, except future passenger arrivals: the
    /// scenario's traffic is replaced by whoever calls <see cref="AssignRequest"/>.
    /// </summary>
    public static ElevatorSystem FromEngine(EngineHandover handover, RuntimeOptions options, Action<long, DomainEvent>? sink = null) =>
        new(options, sink, handover);

    private void TakeOver(EngineHandover handover)
    {
        var elevators = handover.Handlers.Count(h => h.Address.Kind == ActorKind.Elevator);
        if (elevators != _options.Elevators)
            throw new ArgumentException($"the handover has {elevators} elevators but the options say {_options.Elevators}", nameof(handover));
        foreach (var handler in handover.Handlers.OrderBy(h => h.Address.Kind).ThenBy(h => h.Address.Index))
        {
            if (handler is DispatcherHandler dispatcher) DispatchPolicy = dispatcher.Policy.Name;
            Add(new Actor(handler, this, handler.Address.Kind == ActorKind.Dispatcher ? _requests.Reader : null));
        }
        if (!_actors.ContainsKey(Address.Dispatcher) || !_actors.ContainsKey(Address.Passengers))
            throw new ArgumentException("the handover needs a dispatcher and a passengers handler", nameof(handover));

        FaultInjector = new RuntimeFaultInjector(this);
        _logWriter = Task.Run(WriteLog);
        foreach (var envelope in handover.InFlight)
        {
            if (envelope is { Message: PassengerArrives } or { To.Kind: ActorKind.Client }) continue;
            if (!_actors.ContainsKey(envelope.To))
                throw new ArgumentException($"a message in flight is addressed to {envelope.To}, which the runtime does not have", nameof(handover));
            _ = Deliver(envelope, CancellationToken.None).AsTask();
        }
        if (!_options.StartPaused) Resume();
    }

    /// <summary>Starts the actor loops of a system created with <see cref="RuntimeOptions.StartPaused"/>. Idempotent.</summary>
    public void Resume()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        foreach (var actor in _actors.Values) _loops.Add(Supervise(actor.Start(_stop.Token)));
        _loops.Add(Supervise(_delayed.Run(_stop.Token)));
    }

    /// <summary>
    /// Fail-stop supervision (ADR-017). A handler that throws may have left its state half-updated, and the other actors
    /// wait on its replies, so carrying on would serve requests from a broken building. Instead the first exception
    /// stops the whole system: it becomes <see cref="Fault"/>, every pending call fails with it, later calls fail at once,
    /// and the request queue is closed so submitters waiting for room are released too. Nothing hangs.
    /// </summary>
    private async Task Supervise(Task loop)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // stopping
        }
        catch (Exception e)
        {
            OnFault(e);
        }
    }

    private void OnFault(Exception e)
    {
        if (Interlocked.CompareExchange(ref _fault, e, null) is not null) return; // only the first fault is the cause
        var error = new ElevatorSystemFaultedException(e);
        _requests.Writer.TryComplete(error);
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var p))
            {
                Interlocked.Decrement(ref _inFlight);
                p.Tcs.TrySetException(error);
            }
        foreach (var id in _queries.Keys)
            if (_queries.TryRemove(id, out var q)) q.TrySetException(error);
        _stop.Cancel();
    }

    private Exception? _fault;

    private void ThrowIfFaulted()
    {
        if (Fault is { } fault) throw new ElevatorSystemFaultedException(fault);
    }

    /// <summary>Refuses a request this building cannot serve before it reaches any actor (ADR-017).</summary>
    private void Validate(Request request, bool log)
    {
        if (request.Check(_options.Floors) is not { } error) return;
        if (log) Record(new RequestRejected(Clock.Now, "Request", request.PickupFloor, request.Direction, error));
        throw new ArgumentException($"request {request.Id}: {error}", nameof(request));
    }

    private int _started;

    public static ElevatorSystem Start(RuntimeOptions options, Action<long, DomainEvent>? sink = null) => new(options, sink);

    public RealClock Clock { get; }

    public int ElevatorCount => _options.Elevators;

    /// <summary>Injects maintenance, emergency stops and faults (ADR-013), at their sim time on <see cref="Clock"/>.</summary>
    public IFaultInjector FaultInjector { get; private set; } = null!;

    internal CancellationToken Stopping => _stop.Token;

    public string DispatchPolicy { get; private set; } = "";

    public int RequestQueueCapacity => _options.RequestQueueCapacity;

    /// <summary>Requests submitted and not yet assigned.</summary>
    public int PendingAssignments => _pending.Count;

    private int _inFlight;

    /// <summary>
    /// Requests submitted by callers and not yet answered: a plain counter, cheap enough to sample every millisecond
    /// (ConcurrentDictionary.Count takes every lock). The throughput ramp's Little's-law check reads it (ADR-015).
    /// </summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Submits <paramref name="request"/> and completes when an elevator has accepted it. Thread-safe; any number of
    /// callers. Waits (asynchronously) while the request queue is full. Throws <see cref="ArgumentException"/> (logged as
    /// <see cref="RequestRejected"/>) when the request is invalid for this building, <see cref="RequestRefusedException"/>
    /// when no elevator may serve it (access policies, ADR-012), and <see cref="ElevatorSystemFaultedException"/> when the
    /// system has stopped after a handler failure (ADR-017).
    /// </summary>
    public async Task<Assignment> AssignRequest(Request request, CancellationToken ct = default)
    {
        Validate(request, log: true);
        ThrowIfFaulted();
        var started = Stopwatch.GetTimestamp();
        var tcs = new TaskCompletionSource<Assignment>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, (tcs, started)))
            throw new InvalidOperationException($"request {request.Id} is already pending");
        Interlocked.Increment(ref _inFlight);
        // A fault between the check above and TryAdd: OnFault sets the fault before sweeping, so either its sweep saw
        // this entry or this read sees the fault. Both are full fences.
        if (Fault is { } fault)
        {
            if (_pending.TryRemove(request.Id, out _)) Interlocked.Decrement(ref _inFlight);
            throw new ElevatorSystemFaultedException(fault);
        }

        // Backpressure stays with the caller (spec requestQueue): wait while the bounded request queue is full.
        try
        {
            if (!await _requests.Writer.WaitToWriteAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("the system is stopping");
        }
        catch
        {
            if (_pending.TryRemove(request.Id, out _)) Interlocked.Decrement(ref _inFlight);
            throw;
        }
        // Causality by construction (ADR-015): the passengers actor logs PassengerArrived and then submits the request,
        // in one handler call, so nothing about the request can be logged before its passenger. (Registering the passenger
        // and writing the request from here let the dispatcher log it first whenever the passengers actor was busy.)
        _actors[Address.Passengers].Mailbox.Writer.TryWrite(new PassengerArrives(request));
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
        Validate(request, log: false); // a query changes nothing, so it leaves nothing in the log
        ThrowIfFaulted();
        var id = Interlocked.Increment(ref _nextQuery);
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queries[id] = tcs;
        if (Fault is { } fault && _queries.TryRemove(id, out _)) throw new ElevatorSystemFaultedException(fault); // faulted meanwhile
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
    /// for fault and operator commands, as <c>SimEngine.Post</c> in the simulator). An emergency stop goes to the actor's
    /// priority control channel, ahead of its mailbox (ADR-013).
    /// </summary>
    public void Post(Address to, Message message) => Route(to, message);

    /// <summary>
    /// Delivers <paramref name="message"/> to <paramref name="to"/> at sim time <paramref name="at"/> on this system's
    /// clock, in order of (at, call order) with everything else scheduled (ADR-015). The fault injector uses it.
    /// </summary>
    internal void PostAt(SimTime at, Address to, Message message) => _delayed.ScheduleAt(at, new Envelope(to, message));

    /// <summary>
    /// The lane a message takes into an actor. Emergency commands preempt: the priority control channel is read before
    /// the mailbox (ADR-013). Stop and clear share that lane, so they keep their order among themselves (ADR-015).
    /// Everything else goes to the mailbox, FIFO.
    /// </summary>
    private void Route(Address to, Message message)
    {
        var actor = _actors[to];
        var channel = message is ApplyFault { Command.Kind: FaultKind.EmergencyStop or FaultKind.EmergencyClear } ? actor.Control : actor.Mailbox;
        channel.Writer.TryWrite(message);
    }

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

    /// <summary>The first exception that killed an actor loop, if any (a handler bug); the system has then stopped (ADR-017).</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

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
            _delayed.Schedule(envelope); // link latency, FIFO per sender for equal delays (ADR-015)
            return;
        }

        switch (envelope)
        {
            case { To.Kind: ActorKind.Client, Message: RequestAssigned assigned }:
                if (_pending.TryRemove(assigned.RequestId, out var p))
                {
                    var completedAt = Stopwatch.GetTimestamp();
                    Interlocked.Decrement(ref _inFlight);
                    if (_options.RetireOnAccept)
                    {
                        // Perf only (ADR-015): the request leaves as soon as it is accepted, so the seeded backlog stays put.
                        var retired = new PassengerRetired(assigned.RequestId, assigned.ElevatorId);
                        _actors[Address.Elevator(assigned.ElevatorId)].Mailbox.Writer.TryWrite(retired);
                        _actors[Address.Passengers].Mailbox.Writer.TryWrite(retired);
                    }
                    p.Tcs.TrySetResult(new Assignment(assigned.RequestId, assigned.ElevatorId, WallDuration.Since(p.Started), completedAt));
                }
                break;
            case { To.Kind: ActorKind.Client, Message: RequestRefused refused }:
                if (_pending.TryRemove(refused.RequestId, out var r))
                {
                    Interlocked.Decrement(ref _inFlight);
                    r.Tcs.TrySetException(new RequestRefusedException(refused.RequestId, refused.Policy, refused.Reason));
                }
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
                Route(envelope.To, envelope.Message);
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
