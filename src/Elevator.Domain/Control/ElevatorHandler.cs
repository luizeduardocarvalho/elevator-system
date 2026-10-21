using Elevator.Domain.Events;
using Elevator.Domain.Faults;

namespace Elevator.Domain.Control;

/// <param name="SnapshotDelayMs">Delay before a published snapshot reaches the dispatcher (models a stale view).</param>
/// <param name="StalenessToleranceMs">Reject as stale if our ETA is this much worse than the dispatcher's estimate.</param>
/// <param name="AgingMs">Requests waiting at least this long since t_call can no longer be rejected as stale.</param>
public sealed record ElevatorHandlerOptions(long SnapshotDelayMs = 0, long StalenessToleranceMs = 10_000, long AgingMs = 60_000)
{
    public static ElevatorHandlerOptions Default { get; } = new();
}

/// <summary>
/// Owns one <see cref="Elevator"/>. Decides whether to accept an assignment, and translates the elevator's events
/// into notices for passengers and the dispatcher. Publishes a snapshot whenever the elevator changed.
///
/// Accept/reject, in priority order:
/// <list type="number">
/// <item>Out of service (draining or parked for maintenance, emergency stop, fault; ADR-013) → reject with that reason
/// (always wins).</item>
/// <item>Capacity → reject (always wins): in Defer mode when full now; in NextTrip mode only if the request can never
/// fit (its load exceeds the capacity), since a full elevator serves it on a later trip (ADR-010).</item>
/// <item>Stale: our own ETA exceeds the dispatcher's estimate by more than the tolerance → reject, unless the
/// assignment is forced or the request is aged (waiting ≥ AgingMs). Aging stops a request bouncing forever.</item>
/// <item>Otherwise accept.</item>
/// </list>
/// </summary>
public sealed class ElevatorHandler(Elevator elevator, ElevatorHandlerOptions? options = null, IEtaEstimator? estimator = null) : IHandler
{
    private readonly ElevatorHandlerOptions _options = options ?? ElevatorHandlerOptions.Default;
    private long _publishedVersion = -1;
    private SimTime? _unstickAt; // a timed Stuck command ends here

    public Elevator Elevator { get; } = elevator;

    public Address Address => Address.Elevator(Elevator.Id);

    public SimTime? NextWakeUp => _unstickAt is { } at && (Elevator.NextDeadline is not { } d || at < d) ? at : Elevator.NextDeadline;

    public void Handle(Message message, SimTime now, Outbox outbox)
    {
        if (_unstickAt is { } unstickAt && unstickAt <= now)
        {
            Elevator.Tick(unstickAt); // the stuck command ends exactly on time, whenever we are woken
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
            case Assign assign:
                OnAssign(assign, now, outbox);
                break;
            case Unassign unassign:
                if (Elevator.Unassign(unassign.RequestId))
                    Release(unassign.RequestId, AssignmentReason.Rebalance, now, outbox);
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

    /// <summary>
    /// Applies a fault or operator command (ADR-013). Only a command that changes something is logged, as
    /// <see cref="FaultCommandApplied"/>, before whatever it causes. The new snapshot is published before the pickups it
    /// hands back, so the dispatcher already sees the elevator out of service when it reassigns them.
    /// </summary>
    private void ApplyCommand(FaultCommand command, SimTime now, Outbox outbox)
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

    /// <summary>Stuck for DurationMs, or until Unstick; a new Stuck command replaces the previous one's end.</summary>
    private bool Stick(FaultCommand command, SimTime now)
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

    private void OnAssign(Assign assign, SimTime now, Outbox outbox)
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
        outbox.Send(Address.Passengers, new AssignmentAcceptedMsg(request.Id, Elevator.Id)); // before any answer below
        Elevator.AssignPickup(request);
        outbox.Send(Address.Dispatcher, new AssignmentAcceptedMsg(request.Id, Elevator.Id, Elevator.Version));
    }

    private AssignmentReason? RejectionReason(Assign assign, SimTime now)
    {
        // Out of service (ADR-013): always wins, even over a forced assignment.
        switch (Elevator.Mode)
        {
            case ElevatorMode.Draining or ElevatorMode.Maintenance: return AssignmentReason.Maintenance;
            case ElevatorMode.Emergency: return AssignmentReason.Emergency;
            case ElevatorMode.Fault: return AssignmentReason.Fault;
        }
        if (Elevator.State == ElevatorState.Maintenance) return AssignmentReason.Maintenance;
        // Defer mode: full now means no. NextTrip mode: a full elevator takes it for a later trip, unless it can never fit.
        if (Elevator.CapacityMode == CapacityMode.Defer ? Elevator.IsFull : assign.Request.Load > Elevator.Capacity)
            return AssignmentReason.Capacity;
        if (assign.Forced || estimator is null || assign.EtaEstimate is not { } estimate) return null;

        var aged = now.Ms - assign.Request.Timestamp.Ms >= _options.AgingMs;
        if (aged) return null;

        var eta = estimator.EstimatePickup(Elevator.Snapshot(), assign.Request, now);
        return eta is { } mine && mine.Ms - estimate.Ms > _options.StalenessToleranceMs ? AssignmentReason.Stale : null;
    }

    private void Release(int requestId, AssignmentReason reason, SimTime now, Outbox outbox)
    {
        outbox.Emit(new AssignmentReleased(now, requestId, Elevator.Id, reason));
        outbox.Send(Address.Dispatcher, new AssignmentReleasedMsg(requestId, Elevator.Id, reason));
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
                case AssignmentReleased released: // full-load bypass, raised by the elevator itself
                    outbox.Send(Address.Dispatcher, new AssignmentReleasedMsg(released.RequestId, Elevator.Id, released.Reason));
                    outbox.Send(Address.Passengers, new AssignmentReleasedMsg(released.RequestId, Elevator.Id, released.Reason));
                    break;
            }
        }
    }
}
