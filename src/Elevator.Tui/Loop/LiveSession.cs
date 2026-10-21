using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Simulation;
using Elevator.Tui.State;

namespace Elevator.Tui.Loop;

/// <summary>
/// The live view's source: owns a <see cref="SimEngine"/> (explanations on, so ASSIGN lines have every cost) and
/// folds the engine's new log entries into the <see cref="BuildingState"/> after each step. The renderer never sees
/// the engine; it gets a ScreenModel built from the state.
///
/// <b>Seek backward = re-simulate</b> (ADR-011): the sim is deterministic, so a fresh engine run at full speed to
/// the target reproduces the past exactly. Operator commands are the one input not in the scenario, so the session
/// records each one with the sim time it was injected at and re-injects it at that same time on the way. Commands
/// after the target are discarded: going back and continuing starts a new timeline.
/// </summary>
public sealed class LiveSession : ISession
{
    private readonly Scenario _scenario;
    private readonly Strategy? _strategy;
    private readonly Func<SimEngine, IFaultInjector>? _injector;
    private readonly List<FaultCommand> _commands = [];
    private int _folded;

    /// <param name="faultInjector">Builds the engine's injector (the Hard stage's SimFaultInjector); null = no-op.</param>
    public LiveSession(Scenario scenario, Strategy? strategy = null, Func<SimEngine, IFaultInjector>? faultInjector = null)
    {
        _scenario = scenario;
        _strategy = strategy;
        _injector = faultInjector;
        (Engine, State) = Fresh();
        Fold(); // SimulationStarted is logged at Create: the first frame already shows the building
    }

    public string Source => "LIVE";

    /// <summary>The engine (for writing its log at exit; read-only use).</summary>
    public SimEngine Engine { get; private set; }

    public BuildingState State { get; private set; }

    public SimTime Now => Engine.Now;

    public bool AtEnd => Engine.Finished;

    public bool ReadOnly => false;

    public bool InjectorNoOp => Engine.FaultInjector.IsNoOp;

    /// <summary>Operator commands injected so far, in order (re-applied on a backward seek).</summary>
    public IReadOnlyList<FaultCommand> Commands => _commands;

    /// <summary>The full event log so far (SimulationEnded included once finished).</summary>
    public IReadOnlyList<DomainEvent> Log => Engine.Log;

    private (SimEngine, BuildingState) Fresh()
    {
        _folded = 0;
        return (SimEngine.Create(_scenario, _strategy, new SimEngineOptions(EmitDispatchEvaluations: true, _injector)), new BuildingState());
    }

    public void Step()
    {
        if (Engine.Finished) return;
        Engine.AdvanceTo(Engine.Now.AddMs(Pacer.TickMs));
        Fold();
    }

    public void SeekTo(SimTime target)
    {
        if (target < SimTime.Zero) target = SimTime.Zero;
        // Stay on the tick grid so stepping after the seek makes the same AdvanceTo calls as a run that never seeked.
        target = new SimTime(target.Ms / Pacer.TickMs * Pacer.TickMs);
        if (target < Engine.Now)
        {
            var replay = _commands.Where(c => c.At <= target).ToList();
            _commands.Clear();
            (Engine, State) = Fresh();
            foreach (var command in replay)
            {
                Engine.AdvanceTo(command.At);
                Inject(command);
            }
        }
        Engine.AdvanceTo(target);
        Fold();
    }

    public string Command(TuiKey key, int elevator)
    {
        if (elevator < 0 || elevator >= _scenario.Elevators) return $"no elevator E{elevator}";
        var mode = elevator < State.Elevators.Count ? State.Elevators[elevator].Mode : ElevatorMode.Normal;
        FaultCommand? command = key switch
        {
            TuiKey.Maintenance => new FaultCommand(Engine.Now, elevator,
                mode is ElevatorMode.Draining or ElevatorMode.Maintenance ? FaultKind.MaintenanceOff : FaultKind.MaintenanceOn),
            TuiKey.Emergency => new FaultCommand(Engine.Now, elevator,
                mode == ElevatorMode.Emergency ? FaultKind.EmergencyClear : FaultKind.EmergencyStop),
            TuiKey.Stuck => new FaultCommand(Engine.Now, elevator, FaultKind.Stuck, 60_000),
            _ => null,
        };
        if (command is null) return "";
        Inject(command);
        return $"{FaultKinds.Name(command.Kind)} E{elevator} at {View.ViewModel.Clock(command.At)}{(InjectorNoOp ? " (no-op)" : "")}";
    }

    private void Inject(FaultCommand command)
    {
        _commands.Add(command);
        Engine.FaultInjector.Inject(command);
    }

    private void Fold()
    {
        if (Engine.Finished) Engine.Finish(); // idempotent: logs SimulationEnded once, like SimEngine.Run
        var log = Engine.Log;
        for (; _folded < log.Count; _folded++) State.Apply(log[_folded]);
        State.AdvanceTo(Engine.Now);
    }
}
