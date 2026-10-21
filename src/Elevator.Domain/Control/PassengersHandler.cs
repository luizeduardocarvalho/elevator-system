using Elevator.Domain.Events;

namespace Elevator.Domain.Control;

/// <summary>
/// The environment: people waiting, riding and getting off. Not control logic, but it lives behind the same
/// protocol so both runtimes simulate passengers identically.
///
/// Real behaviour: a passenger boards only the elevator they were assigned to (destination dispatch shows it on the
/// hall panel), only when its lantern shows their direction, in arrival order per floor, while there is room
/// (hard capacity). Anyone who does not fit is left behind and presses again (same request id and t_call).
/// </summary>
public sealed class PassengersHandler(int capacity) : IHandler
{
    private sealed class Passenger(Request request)
    {
        public Request Request { get; } = request;
        public int? AssignedTo { get; set; }
        public int? RidingOn { get; set; }
    }

    private readonly List<Passenger> _waiting = []; // arrival order
    private readonly Dictionary<int, Passenger> _byId = [];
    private readonly Dictionary<int, List<Passenger>> _riding = [];

    public Address Address => Address.Passengers;

    public SimTime? NextWakeUp => null;

    public int Arrived { get; private set; }

    public int Delivered { get; private set; }

    public bool Empty => _byId.Count == 0;

    public void Handle(Message message, SimTime now, Outbox outbox)
    {
        switch (message)
        {
            case PassengerArrives arrives:
                Arrive(arrives.Request, now, outbox);
                outbox.Send(Address.Dispatcher, new SubmitRequest(arrives.Request));
                break;
            case PassengerWaiting waiting:
                Arrive(waiting.Request, now, outbox);
                break;
            case AssignmentAcceptedMsg accepted when _byId.GetValueOrDefault(accepted.RequestId) is { RidingOn: null } waiting:
                waiting.AssignedTo = accepted.ElevatorId;
                break;
            case AssignmentReleasedMsg released when _byId.GetValueOrDefault(released.RequestId) is { } waiting
                                                     && waiting.AssignedTo == released.ElevatorId:
                waiting.AssignedTo = null;
                break;
            case ElevatorNotice { Event: DoorsOpened opened }:
                GetOff(opened, now, outbox);
                break;
            case ElevatorNotice { Event: HallCallAnswered answered }:
                GetOn(answered, now, outbox);
                break;
        }
    }

    private void Arrive(Request request, SimTime now, Outbox outbox)
    {
        var p = new Passenger(request);
        _waiting.Add(p);
        _byId.Add(request.Id, p);
        Arrived++;
        outbox.Emit(new PassengerArrived(now, request.Id, request.PickupFloor, request.DestinationFloor));
    }

    private void GetOff(DoorsOpened opened, SimTime now, Outbox outbox)
    {
        if (!_riding.TryGetValue(opened.ElevatorId, out var riders)) return;
        foreach (var p in riders.Where(r => r.Request.DestinationFloor == opened.Floor).ToList())
        {
            riders.Remove(p);
            _byId.Remove(p.Request.Id);
            Delivered++;
            outbox.Emit(new PassengerAlighted(now, p.Request.Id, opened.ElevatorId, opened.Floor));
            outbox.Send(Address.Elevator(opened.ElevatorId), new Alighted(p.Request.Id));
        }
    }

    private void GetOn(HallCallAnswered answered, SimTime now, Outbox outbox)
    {
        var candidates = _waiting
            .Where(p => p.AssignedTo == answered.ElevatorId && p.Request.PickupFloor == answered.Floor && p.Request.Direction == answered.Direction)
            .ToList(); // arrival order
        if (candidates.Count == 0) return;

        var riders = _riding.TryGetValue(answered.ElevatorId, out var list) ? list : _riding[answered.ElevatorId] = [];
        var load = riders.Sum(r => r.Request.Load);
        foreach (var p in candidates)
        {
            if (load + p.Request.Load <= capacity)
            {
                load += p.Request.Load;
                _waiting.Remove(p);
                p.RidingOn = answered.ElevatorId;
                riders.Add(p);
                outbox.Emit(new PassengerBoarded(now, p.Request.Id, answered.ElevatorId, answered.Floor));
                outbox.Send(Address.Elevator(answered.ElevatorId), new Boarded(p.Request));
                outbox.Send(Address.Dispatcher, new RequestPickedUp(p.Request.Id, answered.ElevatorId));
            }
            else
            {
                p.AssignedTo = null;
                outbox.Emit(new PassengerLeftBehind(now, p.Request.Id, answered.ElevatorId, answered.Floor));
                outbox.Send(Address.Dispatcher, new ReRegister(p.Request.Id, AssignmentReason.LeftBehind));
            }
        }
    }
}
