using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Policy;
using Elevator.Policy.Dispatch;
using Elevator.Runtime;

namespace Elevator.Simulation;

public sealed record SimulationResult(IReadOnlyList<DomainEvent> Events, int Delivered, int Unserved, SimTime EndedAt);

/// <param name="EmitDispatchEvaluations">Log every candidate's cost per dispatch decision (DispatchEvaluated). Off by
/// default.</param>
/// <param name="FaultInjector">Builds the fault injector for this engine. Null = <see cref="NoOpFaultInjector"/>.</param>
public sealed record SimEngineOptions(bool EmitDispatchEvaluations = false, Func<SimEngine, IFaultInjector>? FaultInjector = null)
{
    public static SimEngineOptions Default { get; } = new();
}

/// <summary>
/// The deterministic shell (ADR-005): delivers protocol messages to the shared handlers from one priority queue
/// ordered by (sim time, priority lane, sequence number). No threads, no decisions; same handlers as the actor runtime.
///
/// The outer loop still moves a <see cref="SimClock"/> in fixed ticks, but a tick only
/// bounds how far the queue is drained, so the event log is identical for any tick size.
/// </summary>
public sealed class SimEngine
{
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
        var scheduling = new LookScheduling();
        var dispatch = DispatchPolicies.Create(strategy.Dispatch, scheduling, scenario.Timing);
        var latency = scenario.Latency ?? MessageLatency.None;
        var estimator = new StopProjection(scheduling, scenario.Timing);

        var fleet = scenario.EffectiveFleet;
        if (fleet.Count != scenario.Elevators)
            throw new ArgumentException($"the fleet has {fleet.Count} elevators but the scenario says {scenario.Elevators}", nameof(scenario));
        for (var i = 0; i < scenario.Elevators; i++)
        {
            var elevator = fleet[i].CreateElevator(i, scenario.Floors, scheduling, scenario.BypassThreshold);
            var handler = new ElevatorHandler(elevator,
                new ElevatorHandlerOptions(SnapshotDelayMs: latency.SnapshotMs, ReportProgress: scenario.WatchdogTimeoutMs is not null), estimator);
            _elevators.Add(handler);
            Register(handler);
        }
        Register(new DispatcherHandler(dispatch, scenario.Elevators, scenario.Timing,
            new DispatcherOptions(latency.DispatchMs, OverloadThreshold: RuntimeOptions.DefaultOverloadThreshold, EmitDispatchEvaluations: options.EmitDispatchEvaluations)
            {
                Access = scenario.Access.CreatePipeline(),
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
            scenario.BypassThreshold, "next-trip",
            scenario.Fleet is null ? null : [.. fleet.Select((t, i) => new FleetEntry(i, t.Name, t.Capacity, scenario.BypassThreshold, t.ServedFloors))]));

        Register(new FaultScheduler());

        foreach (var e in _elevators) Enqueue(SimTime.Zero, new Envelope(e.Address, new Start()));
        foreach (var p in passengers) Enqueue(SimTime.Zero, new Envelope(Address.Passengers, new PassengerArrives(p.ToRequest()), p.ArrivesAt.Ms));
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
    public Handover HandOver()
    {
        if (_handedOver) throw new InvalidOperationException("already handed over");
        _handedOver = true;
        var inFlight = _queue.UnorderedItems
            .Where(item => item.Element.Message is not WakeUp)
            .OrderBy(item => item.Priority)
            .Select(item => item.Element with { DelayMs = Math.Max(0, item.Priority.TimeMs - Now.Ms) })
            .ToList();
        return new Handover([.. _handlers.Values.Where(h => h.Address.Kind != ActorKind.FaultScheduler)], Now, inFlight);
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
        if (envelope.To.Kind == ActorKind.Client) return;
        _queue.Enqueue(envelope, (now.Ms + envelope.DelayMs, PriorityOf(envelope.Message), _seq++));
    }

    private static int PriorityOf(Message message) =>
        message is ApplyFault { Command.Kind: FaultKind.EmergencyStop or FaultKind.EmergencyClear }
            or ScheduleFault { Command.Kind: FaultKind.EmergencyStop or FaultKind.EmergencyClear } ? 0 : 1;

    private bool AllDone =>
        _passengers.Arrived == _expectedPassengers && _passengers.Delivered + _passengers.Refused == _expectedPassengers
        && _elevators.All(e => e.Elevator.State is ElevatorState.Idle or ElevatorState.Maintenance && e.Elevator.NextDeadline is null);

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
