using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Control;

/// <param name="SnapshotDelayMs">Delay before a published snapshot reaches the dispatcher (models a stale view).</param>
/// <param name="StalenessToleranceMs">Reject as stale if our ETA is this much worse than the dispatcher's estimate.</param>
/// <param name="AgingMs">Requests waiting at least this long since t_call can no longer be rejected as stale.</param>
/// <param name="ReportProgress">Tell the dispatcher about every floor reached (<see cref="ProgressReported"/>), for its
/// watchdog (ADR-013).</param>
public sealed record ElevatorControllerOptions(long SnapshotDelayMs = 0, long StalenessToleranceMs = 10_000, long AgingMs = 60_000,
    bool ReportProgress = false)
{
    public static ElevatorControllerOptions Default { get; } = new();
}

/// <summary>
/// The spec's <c>ElevatorController</c>: per-elevator control (ADR-018). Owns one <see cref="Elevator"/>. Decides whether to accept an assignment, and translates the elevator's events
/// into notices for passengers and the dispatcher. Publishes a snapshot whenever the elevator changed.
///
/// Accept/reject, in priority order:
/// <list type="number">
/// <item>Out of service (draining or parked for maintenance, emergency stop, fault; ADR-013) → reject with that reason
/// (always wins).</item>
/// <item>Capacity → reject (always wins) only if the request can never fit (its load exceeds the capacity): a full
/// elevator serves it on a later trip (ADR-010).</item>
/// <item>Stale: our own ETA exceeds the dispatcher's estimate by more than the tolerance → reject, unless the
/// assignment is forced or the request is aged (waiting ≥ AgingMs). Aging stops a request bouncing forever.</item>
/// <item>Otherwise accept.</item>
/// </list>
/// </summary>
public sealed class ElevatorController(Elevator elevator, ElevatorControllerOptions? options = null, IEtaEstimator? estimator = null) : IHandler
{
    private readonly ElevatorControllerOptions _options = options ?? ElevatorControllerOptions.Default;
    private long _publishedVersion = -1;
    private Instant? _unstickAt;

    public Elevator Elevator { get; } = elevator;

    public Address Address => Address.Elevator(Elevator.Id);

    public Instant? NextWakeUp => _unstickAt is { } at && (Elevator.NextDeadline is not { } d || at < d) ? at : Elevator.NextDeadline;

    public void Handle(Message message, Instant now, Outbox outbox)
    {
        if (_unstickAt is { } unstickAt && unstickAt <= now)
        {
            Elevator.Tick(unstickAt);
            _unstickAt = null;
            Elevator.Unstick();
        }
        Elevator.Tick(now);
        Flush(outbox);

        switch (message)
        {
            case ApplyFault fault:
                ApplyCommand(fault.Command, now, outbox);
                break;
            case FaultDeclared declared:
                Elevator.DeclareFault(declared.SilentForMs);
                Publish(outbox);
                Flush(outbox);
                break;
            case Assign assign:
                OnAssign(assign, now, outbox);
                break;
            case PassengerRetired retired:
                Elevator.Unassign(retired.RequestId); // no release: a released pickup would be re-dispatched (perf only, ADR-015)
                break;
            case Boarded boarded:
                Elevator.Board(boarded.Rider);
                break;
            case Alighted alighted:
                Elevator.Alight(alighted.RequestId);
                break;
        }

        Flush(outbox);
        Publish(outbox);
    }

    private void Publish(Outbox outbox)
    {
        if (Elevator.Version == _publishedVersion) return;
        _publishedVersion = Elevator.Version;
        outbox.Send(Address.Dispatcher, new SnapshotPublished(Elevator.Snapshot()), _options.SnapshotDelayMs);
    }

    private void ApplyCommand(FaultCommand command, Instant now, Outbox outbox)
    {
        var applied = command.Kind switch
        {
            FaultKind.Stuck => Stick(command, now),
            FaultKind.Unstick => Unstick(),
            FaultKind.MaintenanceOn => Elevator.RequestMaintenance(),
            FaultKind.MaintenanceOff => Elevator.EndMaintenance(),
            FaultKind.EmergencyStop => Elevator.EmergencyStop(),
            FaultKind.EmergencyClear => Elevator.ClearEmergency(),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "unknown fault kind"),
        };
        if (!applied) return;
        outbox.Emit(new FaultCommandApplied(now, Elevator.Id, command.Kind, command.DurationMs));
        Publish(outbox);
        Flush(outbox);
    }

    private bool Stick(FaultCommand command, Instant now)
    {
        Elevator.Stick();
        _unstickAt = command.DurationMs is { } ms ? now.AddMs(ms) : null;
        return true;
    }

    private bool Unstick()
    {
        _unstickAt = null;
        return Elevator.Unstick();
    }

    private void OnAssign(Assign assign, Instant now, Outbox outbox)
    {
        var request = assign.Request;
        var reason = RejectionReason(assign, now);
        if (reason is { } r)
        {
            outbox.Emit(new AssignmentRejected(now, request.Id, Elevator.Id, r));
            outbox.Send(Address.Dispatcher, new AssignmentRejectedMsg(request.Id, Elevator.Id, r));
            return;
        }

        outbox.Emit(new AssignmentAccepted(now, request.Id, Elevator.Id));
        outbox.Send(Address.Passengers, new AssignmentAcceptedMsg(request.Id, Elevator.Id));
        Elevator.AssignPickup(request);
        outbox.Send(Address.Dispatcher, new AssignmentAcceptedMsg(request.Id, Elevator.Id, Elevator.Version));
    }

    private AssignmentReason? RejectionReason(Assign assign, Instant now)
    {
        switch (Elevator.Mode)
        {
            case ElevatorMode.Draining or ElevatorMode.Maintenance: return AssignmentReason.Maintenance;
            case ElevatorMode.Emergency: return AssignmentReason.Emergency;
            case ElevatorMode.Fault: return AssignmentReason.Fault;
        }
        if (Elevator.State == ElevatorState.Maintenance) return AssignmentReason.Maintenance;
        if (assign.Request.Load > Elevator.Capacity)
            return AssignmentReason.Capacity;
        if (assign.Forced || estimator is null || assign.EtaEstimate is not { } estimate) return null;

        var aged = now.Ms - assign.Request.Timestamp.Ms >= _options.AgingMs;
        if (aged) return null;

        var eta = estimator.EstimatePickup(Elevator.Snapshot(), assign.Request, now);
        return eta is { } mine && mine.Ms - estimate.Ms > _options.StalenessToleranceMs ? AssignmentReason.Stale : null;
    }

    private void Release(int requestId, AssignmentReason reason, Instant now, Outbox outbox)
    {
        outbox.Emit(new AssignmentReleased(now, requestId, Elevator.Id, reason));
        SendRelease(requestId, reason, outbox);
    }

    private void SendRelease(int requestId, AssignmentReason reason, Outbox outbox)
    {
        Publish(outbox);
        outbox.Send(Address.Dispatcher, new AssignmentReleasedMsg(requestId, Elevator.Id, reason), _options.SnapshotDelayMs);
        outbox.Send(Address.Passengers, new AssignmentReleasedMsg(requestId, Elevator.Id, reason));
    }

    private void Flush(Outbox outbox)
    {
        foreach (var e in Elevator.DrainEvents())
        {
            outbox.Emit(e);
            switch (e)
            {
                case DoorsOpened or HallCallAnswered:
                    outbox.Send(Address.Passengers, new ElevatorNotice(e));
                    break;
                case FloorReached reached when _options.ReportProgress:
                    outbox.Send(Address.Dispatcher, new ProgressReported(Elevator.Id, reached.Floor, reached.At), _options.SnapshotDelayMs);
                    break;
                case AssignmentReleased released:
                    SendRelease(released.RequestId, released.Reason, outbox);
                    break;
            }
        }
    }
}
