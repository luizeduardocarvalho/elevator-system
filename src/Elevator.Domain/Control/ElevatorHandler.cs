using Elevator.Domain.Events;

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
/// <item>Maintenance → reject (always wins).</item>
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

    public Elevator Elevator { get; } = elevator;

    public Address Address => Address.Elevator(Elevator.Id);

    public SimTime? NextWakeUp => Elevator.NextDeadline;

    public void Handle(Message message, SimTime now, Outbox outbox)
    {
        Elevator.Tick(now);
        Flush(outbox);

        switch (message)
        {
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
        if (Elevator.Version != _publishedVersion)
        {
            _publishedVersion = Elevator.Version;
            outbox.Send(Address.Dispatcher, new SnapshotPublished(Elevator.Snapshot()), _options.SnapshotDelayMs);
        }
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
        outbox.Send(Address.Dispatcher, new AssignmentAcceptedMsg(request.Id, Elevator.Id));
        outbox.Send(Address.Passengers, new AssignmentAcceptedMsg(request.Id, Elevator.Id)); // before any answer below
        Elevator.AssignPickup(request);
    }

    private AssignmentReason? RejectionReason(Assign assign, SimTime now)
    {
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
