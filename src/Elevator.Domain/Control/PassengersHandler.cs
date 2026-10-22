using Elevator.Domain.Events;

namespace Elevator.Domain.Control;

/// <summary>
/// The environment: people waiting, riding and getting off. Not control logic, but it lives behind the same
/// protocol so both runtimes simulate passengers identically.
///
/// Real behaviour: a passenger boards only the elevator they were assigned to (destination dispatch shows it on the
/// hall panel), only when its lantern shows their direction, in arrival order per floor, while there is room
/// (hard capacity). Anyone who does not fit is left behind and presses again (same request id and t_call), and so is
/// everyone behind them: nobody overtakes in the queue, also when a heavy load does not fit but a lighter one would
/// (ADR-012). With one-unit loads that is the same thing.
/// </summary>
public sealed class PassengersHandler : IHandler
{
    private readonly Func<int, int> _capacityOf;

    /// <param name="capacity">Every elevator's capacity (load units).</param>
    public PassengersHandler(int capacity) => _capacityOf = _ => capacity;

    /// <param name="capacities">Each elevator's capacity by id (elevator types, ADR-012).</param>
    public PassengersHandler(IReadOnlyList<int> capacities)
    {
        var copy = capacities.ToArray();
        _capacityOf = id => copy[id];
    }

    private sealed class Passenger(Request request)
    {
        public Request Request { get; } = request;
        public int? AssignedTo { get; set; }
        public int? RidingOn { get; set; }
    }

    private readonly Dictionary<(int Floor, Direction Direction), List<Passenger>> _waiting = []; // per floor and direction, arrival order
    private readonly Dictionary<int, Passenger> _byId = [];
    private readonly Dictionary<int, List<Passenger>> _riding = [];

    public Address Address => Address.Passengers;

    public SimTime? NextWakeUp => null;

    /// <summary>People waiting per floor and direction (shared v3 contract: the TUI's ▲/▼ counts).</summary>
    public IReadOnlyDictionary<(int Floor, Direction Direction), int> WaitingCounts =>
        _waiting.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Count);

    public int Arrived { get; private set; }

    public int Delivered { get; private set; }

    /// <summary>Passengers whose request every elevator's access policies refused (ADR-012); they left.</summary>
    public int Refused { get; private set; }

    /// <summary>Passengers retired on acceptance by the throughput ramp (perf only, ADR-015).</summary>
    public int Retired { get; private set; }

    public bool Empty => _byId.Count == 0;

    public void Handle(Message message, SimTime now, Outbox outbox)
    {
        switch (message)
        {
            case PassengerArrives arrives:
                Arrive(arrives.Request, now, outbox);
                outbox.Send(Address.Dispatcher, new SubmitRequest(arrives.Request));
                break;
            case AssignmentAcceptedMsg accepted when _byId.GetValueOrDefault(accepted.RequestId) is { RidingOn: null } waiting:
                waiting.AssignedTo = accepted.ElevatorId;
                break;
            case PassengerRetired retired when _byId.GetValueOrDefault(retired.RequestId) is { RidingOn: null } leaving:
                _waiting[(leaving.Request.PickupFloor, leaving.Request.Direction)].Remove(leaving);
                _byId.Remove(retired.RequestId);
                Retired++;
                outbox.Send(Address.Dispatcher, new RequestPickedUp(retired.RequestId, retired.ElevatorId));
                break;
            case AssignmentReleasedMsg released when _byId.GetValueOrDefault(released.RequestId) is { } waiting
                                                     && waiting.AssignedTo == released.ElevatorId:
                waiting.AssignedTo = null;
                break;
            case RequestRefused refused when _byId.GetValueOrDefault(refused.RequestId) is { RidingOn: null } waiting:
                _waiting[(waiting.Request.PickupFloor, waiting.Request.Direction)].Remove(waiting);
                _byId.Remove(refused.RequestId);
                Refused++;
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
        var key = (request.PickupFloor, request.Direction);
        (_waiting.TryGetValue(key, out var list) ? list : _waiting[key] = []).Add(p);
        _byId.Add(request.Id, p);
        Arrived++;
        outbox.Emit(new PassengerArrived(now, request.Id, request.PickupFloor, request.DestinationFloor,
            Load: request.Load != 1 ? request.Load : null,           // v3.1: written only when set, so default logs
            Vip: request.Vip ? true : null,                          // stay byte-identical
            AccessLevel: request.AccessLevel > 0 ? request.AccessLevel : null));
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
        if (!_waiting.TryGetValue((answered.Floor, answered.Direction), out var here)) return;
        var candidates = here.Where(p => p.AssignedTo == answered.ElevatorId).ToList(); // arrival order
        if (candidates.Count == 0) return;

        var riders = _riding.TryGetValue(answered.ElevatorId, out var list) ? list : _riding[answered.ElevatorId] = [];
        var load = riders.Sum(r => r.Request.Load);
        var capacity = _capacityOf(answered.ElevatorId);
        var blocked = false; // strict arrival order: once someone does not fit, nobody behind them overtakes (ADR-012)
        foreach (var p in candidates)
        {
            if (!blocked && load + p.Request.Load <= capacity)
            {
                load += p.Request.Load;
                here.Remove(p);
                p.RidingOn = answered.ElevatorId;
                riders.Add(p);
                outbox.Emit(new PassengerBoarded(now, p.Request.Id, answered.ElevatorId, answered.Floor));
                outbox.Send(Address.Elevator(answered.ElevatorId), new Boarded(p.Request));
                outbox.Send(Address.Dispatcher, new RequestPickedUp(p.Request.Id, answered.ElevatorId));
            }
            else
            {
                blocked = true;
                p.AssignedTo = null;
                outbox.Emit(new PassengerLeftBehind(now, p.Request.Id, answered.ElevatorId, answered.Floor));
                outbox.Send(Address.Dispatcher, new ReRegister(p.Request.Id, AssignmentReason.LeftBehind));
            }
        }
    }
}
