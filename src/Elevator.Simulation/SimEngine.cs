using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Policy;
using Elevator.Policy.Dispatch;

namespace Elevator.Simulation;

/// <summary>
/// What a simulation hands to the actor runtime (ADR-015): its handler objects (elevators, dispatcher, passengers) as
/// they are at <paramref name="At"/>, and the messages still in flight, each with its remaining delay. Ownership moves
/// once, before any thread starts: the engine refuses to run afterwards, so a handler never has two owners.
/// </summary>
public sealed record EngineHandover(IReadOnlyList<IHandler> Handlers, SimTime At, IReadOnlyList<Envelope> InFlight);

public sealed record SimulationResult(IReadOnlyList<DomainEvent> Events, int Delivered, int Unserved, SimTime EndedAt);

/// <param name="EmitDispatchEvaluations">Log every candidate's cost per dispatch decision (DispatchEvaluated). Off by
/// default: logs are then identical to v2.</param>
/// <param name="FaultInjector">Builds the fault injector for this engine (Hard stage). Null = <see cref="NoOpFaultInjector"/>.</param>
public sealed record SimEngineOptions(bool EmitDispatchEvaluations = false, Func<SimEngine, IFaultInjector>? FaultInjector = null)
{
    public static SimEngineOptions Default { get; } = new();
}

/// <summary>
/// The deterministic shell (ADR-005): delivers protocol messages to the shared handlers from one priority queue
/// ordered by (sim time, sequence number). No threads, no decisions; same handlers as the actor runtime.
///
/// The outer loop still moves a <see cref="SimClock"/> in fixed ticks (the future TUI's frame), but a tick only
/// bounds how far the queue is drained, so the event log is identical for any tick size.
/// </summary>
public sealed class SimEngine
{
    /// <summary>
    /// Requests in flight above which the dispatcher falls back to Nearest (ADR-006). The most ever seen in the bench
    /// profiles is 92 (FIFO saturating at down-peak), so 200 never triggers in normal operation.
    /// </summary>
    public const int OverloadThreshold = 200;

    // Ordered by (sim time, priority, sequence). Priority 0 is only for emergency stops (ADR-013): they take effect
    // before anything else due at the same instant, as the actor runtime's priority control channel does.
    private readonly PriorityQueue<Envelope, (long TimeMs, int Priority, long Seq)> _queue = new();
    private readonly Dictionary<Address, IHandler> _handlers = [];
    private readonly Dictionary<Address, SimTime> _scheduledWakeUps = [];
    private readonly List<ElevatorHandler> _elevators = [];
    private readonly PassengersHandler _passengers;
    private readonly Outbox _outbox = new();
    private readonly int _expectedPassengers;
    private long _seq;

    private bool _ended;
    private bool _handedOver;

    /// <summary>Runs the scenario to the end in fixed ticks (the headless path used by run and bench).</summary>
    public static SimulationResult Run(Scenario scenario, Strategy? strategy = null, long tickMs = ScenarioRunner.DefaultTickMs)
    {
        if (tickMs <= 0) throw new ArgumentOutOfRangeException(nameof(tickMs));
        var engine = Create(scenario, strategy);
        while (!engine.Finished) engine.AdvanceTo(engine.Now.AddMs(tickMs));
        return engine.Finish();
    }

    /// <summary>
    /// A steppable engine (shared v3 contract): drive it with <see cref="AdvanceTo"/> in any steps (the TUI's fixed
    /// tick) and the event log is identical to <see cref="Run"/>'s; end it with <see cref="Finish"/>.
    /// </summary>
    public static SimEngine Create(Scenario scenario, Strategy? strategy = null, SimEngineOptions? options = null) =>
        new(scenario, strategy ?? new Strategy(scenario.Dispatch), options ?? SimEngineOptions.Default);

    private SimEngine(Scenario scenario, Strategy strategy, SimEngineOptions options)
    {
        Scenario = scenario;
        Strategy = strategy;
        Options = options;
        var scheduling = new LookScheduling(); // ADR-004
        var dispatch = DispatchPolicies.Create(strategy.Dispatch, scheduling, scenario.Timing);
        var latency = scenario.Latency ?? MessageLatency.None;
        var estimator = new StopProjection(scheduling, scenario.Timing); // each elevator's own view, for the staleness check

        var fleet = scenario.EffectiveFleet;
        if (fleet.Count != scenario.Elevators)
            throw new ArgumentException($"the fleet has {fleet.Count} elevators but the scenario says {scenario.Elevators}", nameof(scenario));
        for (var i = 0; i < scenario.Elevators; i++)
        {
            var elevator = Building.CreateElevator(i, scenario.Floors, fleet[i], scheduling, scenario.BypassThreshold);
            var handler = new ElevatorHandler(elevator,
                new ElevatorHandlerOptions(SnapshotDelayMs: latency.SnapshotMs, ReportProgress: scenario.WatchdogTimeoutMs is not null), estimator);
            _elevators.Add(handler);
            Register(handler);
        }
        Register(new DispatcherHandler(dispatch, scenario.Elevators, scenario.Timing,
            new DispatcherOptions(latency.DispatchMs, OverloadThreshold: OverloadThreshold, EmitDispatchEvaluations: options.EmitDispatchEvaluations)
            {
                Access = Building.CreateAccess(scenario.Access),
                WatchdogTimeoutMs = scenario.WatchdogTimeoutMs,
                VipPriority = scenario.Access.VipPriority,
            },
            new NearestDispatch()));
        _passengers = new PassengersHandler([.. fleet.Select(t => t.Capacity)]);
        Register(_passengers);

        var passengers = ScenarioRunner.BuildPassengers(scenario);
        _expectedPassengers = passengers.Count;
        Log.Add(new SimulationStarted(SimTime.Zero, scenario.Name, scenario.Seed, scenario.Floors, scheduling.Name, passengers.Count,
            scenario.Elevators, dispatch.Name, scenario.Capacity,
            scenario.BypassThreshold, "next-trip", // the log keeps the v3 fields; next-trip is the only capacity mode (ADR-018)
            // v3.1: the per-elevator description, only for a configured (typed) fleet; null keeps default logs unchanged.
            scenario.Fleet is null ? null : [.. fleet.Select((t, i) => new FleetEntry(i, t.Name, t.Capacity, scenario.BypassThreshold, t.ServedFloors))]));

        Register(new FaultScheduler());

        foreach (var e in _elevators) Enqueue(SimTime.Zero, new Envelope(e.Address, new Start()));
        foreach (var p in passengers) Enqueue(SimTime.Zero, new Envelope(Address.Passengers, new PassengerArrives(p.ToRequest()), p.ArrivesAt.Ms));
        // The scenario's fault schedule (ADR-013): each command reaches its elevator at its sim time.
        foreach (var f in scenario.FaultSchedule)
        {
            if (f.ElevatorId < 0 || f.ElevatorId >= scenario.Elevators)
                throw new ArgumentException($"fault schedule: elevator {f.ElevatorId} does not exist (0..{scenario.Elevators - 1})", nameof(scenario));
            Enqueue(SimTime.Zero, new Envelope(Address.Elevator(f.ElevatorId), new ApplyFault(f), f.At.Ms));
        }

        FaultInjector = options.FaultInjector?.Invoke(this) ?? NoOpFaultInjector.Instance;
    }

    public Scenario Scenario { get; }

    public Strategy Strategy { get; }

    public SimEngineOptions Options { get; }

    public List<DomainEvent> Log { get; } = [];

    /// <summary>Sim time reached so far (the target of the last <see cref="AdvanceTo"/>, or the finish time).</summary>
    public SimTime Now { get; private set; } = SimTime.Zero;

    /// <summary>Everyone delivered (or refused by access policy) and every elevator idle or parked, or the scenario's max sim time reached.</summary>
    public bool Finished { get; private set; }

    /// <summary>When the run finished (the last event's time, or the max sim time).</summary>
    public SimTime? FinishedAt { get; private set; }

    /// <summary>The injector for this engine (NoOpFaultInjector unless the options supply one).</summary>
    public IFaultInjector FaultInjector { get; }

    /// <summary>Each elevator's latest snapshot, as of its last handled message (read-only; for rendering).</summary>
    public IReadOnlyList<ElevatorSnapshot> Elevators => [.. _elevators.Select(e => e.Elevator.Snapshot())];

    /// <summary>People waiting per floor and direction (read-only; for rendering).</summary>
    public IReadOnlyDictionary<(int Floor, Direction Direction), int> Waiting => _passengers.WaitingCounts;

    public int Delivered => _passengers.Delivered;

    public int ExpectedPassengers => _expectedPassengers;

    /// <summary>
    /// Delivers every message due up to <paramref name="target"/> (capped at the scenario's max sim time). The tick
    /// size never changes the log: messages are handled in (time, sequence) order whatever the steps.
    /// </summary>
    public void AdvanceTo(SimTime target)
    {
        if (_handedOver) throw new InvalidOperationException("this engine handed its handlers over (HandOver) and can no longer run");
        if (Finished) return;
        if (target < Now) throw new ArgumentOutOfRangeException(nameof(target), $"time went backwards: {target} < {Now}");
        target = SimTime.Min(target, Scenario.MaxSimTime);
        if (DrainUntil(target) is { } finishedAt)
        {
            Finished = true;
            FinishedAt = finishedAt;
            Now = finishedAt;
            return;
        }
        Now = target;
        if (Now >= Scenario.MaxSimTime)
        {
            Finished = true;
            FinishedAt = Scenario.MaxSimTime;
        }
    }

    /// <summary>
    /// Delivers <paramref name="message"/> to <paramref name="to"/> at <see cref="Now"/>, after anything already due
    /// at <see cref="Now"/>. The injection seam for fault and operator commands (shared v3 contract). Emergency stops
    /// are the one exception: they go ahead of what is due at the same instant (ADR-013).
    /// </summary>
    public void Post(Address to, Message message) => Enqueue(Now, new Envelope(to, message));

    /// <summary>
    /// Gives up this engine's handlers and in-flight messages, as of <see cref="Now"/>, to another shell (the actor
    /// runtime seeding a steady-state perf run from a busy moment, ADR-015). Wake-ups are left out: a handler's next
    /// wake-up is part of its state (<see cref="IHandler.NextWakeUp"/>). The engine cannot run afterwards.
    /// </summary>
    public EngineHandover HandOver()
    {
        if (_handedOver) throw new InvalidOperationException("already handed over");
        _handedOver = true;
        var inFlight = _queue.UnorderedItems
            .Where(item => item.Element.Message is not WakeUp)
            .OrderBy(item => item.Priority)
            .Select(item => item.Element with { DelayMs = Math.Max(0, item.Priority.TimeMs - Now.Ms) })
            .ToList();
        return new EngineHandover([.. _handlers.Values.Where(h => h.Address.Kind != ActorKind.FaultScheduler)], Now, inFlight);
    }

    /// <summary>Ends the run: logs SimulationEnded and returns the result. Idempotent.</summary>
    public SimulationResult Finish()
    {
        var endedAt = FinishedAt ?? Now;
        var unserved = _expectedPassengers - _passengers.Delivered;
        if (!_ended)
        {
            _ended = true;
            Log.Add(new SimulationEnded(endedAt, _passengers.Delivered, unserved));
        }
        return new SimulationResult(Log, _passengers.Delivered, unserved, endedAt);
    }

    private void Register(IHandler handler) => _handlers.Add(handler.Address, handler);

    private void Enqueue(SimTime now, Envelope envelope)
    {
        if (envelope.To.Kind == ActorKind.Client) return; // nobody waits on acks in a simulation
        _queue.Enqueue(envelope, (now.Ms + envelope.DelayMs, PriorityOf(envelope.Message), _seq++));
    }

    /// <summary>
    /// Emergency commands (stop and clear), on their way to the elevator (via the fault scheduler or directly), jump
    /// ahead of messages due at the same instant: the simulator's equivalent of the actor runtime's priority channel
    /// (ADR-013). Among themselves they keep (time, sequence) order, so a clear posted before a stop stays before it
    /// (ADR-015). Every other message keeps the (time, sequence) order, so <see cref="Post"/> delivers after what is
    /// already due.
    /// </summary>
    private static int PriorityOf(Message message) =>
        message is ApplyFault { Command.Kind: FaultKind.EmergencyStop or FaultKind.EmergencyClear }
            or ScheduleFault { Command.Kind: FaultKind.EmergencyStop or FaultKind.EmergencyClear } ? 0 : 1;

    private bool AllDone =>
        _passengers.Arrived == _expectedPassengers && _passengers.Delivered + _passengers.Refused == _expectedPassengers
        && _elevators.All(e => e.Elevator.State is ElevatorState.Idle or ElevatorState.Maintenance && e.Elevator.NextDeadline is null);

    /// <summary>Delivers every message due at or before <paramref name="target"/>; returns the finish time if the run completed.</summary>
    private SimTime? DrainUntil(SimTime target)
    {
        if (_expectedPassengers == 0) return SimTime.Zero;

        while (_queue.TryPeek(out var envelope, out var key) && key.TimeMs <= target.Ms)
        {
            _queue.Dequeue();
            var now = new SimTime(key.TimeMs);
            var handler = _handlers[envelope.To];

            _outbox.Clear();
            handler.Handle(envelope.Message, now, _outbox);
            Log.AddRange(_outbox.Events);
            foreach (var next in _outbox.Messages) Enqueue(now, next);

            if (handler.NextWakeUp is { } wake && (!_scheduledWakeUps.TryGetValue(handler.Address, out var scheduled) || scheduled != wake))
            {
                _scheduledWakeUps[handler.Address] = wake;
                _queue.Enqueue(new Envelope(handler.Address, new WakeUp()), (wake.Ms, 1, _seq++));
            }

            if (AllDone) return now;
        }
        return null;
    }
}
