namespace Elevator.Domain.Faults;

public enum FaultKind
{
    MaintenanceOn,
    MaintenanceOff,
    EmergencyStop,
    EmergencyClear,
    /// <summary>The elevator stops making progress (for <see cref="FaultCommand.DurationMs"/>, or until Unstick).</summary>
    Stuck,
    Unstick,
}

/// <summary>
/// One fault or operator command for elevator <paramref name="ElevatorId"/>, taking effect at <paramref name="At"/>
/// (sim time), or at the current sim time if that is later, so runs with injected commands stay deterministic.
/// </summary>
/// <param name="DurationMs">Only for <see cref="FaultKind.Stuck"/>: how long it stays stuck; null = until Unstick.</param>
public sealed record FaultCommand(SimTime At, int ElevatorId, FaultKind Kind, long? DurationMs = null);

public interface IFaultInjector
{
    bool IsNoOp { get; }

    void Inject(FaultCommand command);
}

public sealed class NoOpFaultInjector : IFaultInjector
{
    public static NoOpFaultInjector Instance { get; } = new();

    private NoOpFaultInjector() { }

    public bool IsNoOp => true;

    public void Inject(FaultCommand command) { }
}

public static class FaultInjectorExtensions
{
    public static void RequestMaintenance(this IFaultInjector injector, int elevatorId, SimTime at) =>
        injector.Inject(new FaultCommand(at, elevatorId, FaultKind.MaintenanceOn));

    public static void EndMaintenance(this IFaultInjector injector, int elevatorId, SimTime at) =>
        injector.Inject(new FaultCommand(at, elevatorId, FaultKind.MaintenanceOff));

    public static void EmergencyStop(this IFaultInjector injector, int elevatorId, SimTime at) =>
        injector.Inject(new FaultCommand(at, elevatorId, FaultKind.EmergencyStop));

    public static void ClearEmergency(this IFaultInjector injector, int elevatorId, SimTime at) =>
        injector.Inject(new FaultCommand(at, elevatorId, FaultKind.EmergencyClear));

    public static void Stick(this IFaultInjector injector, int elevatorId, SimTime at, long? durationMs = null) =>
        injector.Inject(new FaultCommand(at, elevatorId, FaultKind.Stuck, durationMs));

    public static void Unstick(this IFaultInjector injector, int elevatorId, SimTime at) =>
        injector.Inject(new FaultCommand(at, elevatorId, FaultKind.Unstick));
}
