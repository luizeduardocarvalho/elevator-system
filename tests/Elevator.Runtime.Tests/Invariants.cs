using Elevator.Domain;
using Elevator.Domain.Events;

namespace Elevator.Runtime.Tests;

internal static class Invariants
{
    public static void Check(IReadOnlyList<DomainEvent> events, int capacity)
    {
        var owner = new Dictionary<int, int>();
        foreach (var e in events)
        {
            switch (e)
            {
                case AssignmentAccepted a:
                    Assert.False(owner.ContainsKey(a.RequestId), $"request {a.RequestId} accepted by E{a.ElevatorId} while owned by E{owner.GetValueOrDefault(a.RequestId)}");
                    owner[a.RequestId] = a.ElevatorId;
                    break;
                case AssignmentReleased r:
                    owner.Remove(r.RequestId);
                    break;
                case PassengerLeftBehind l:
                    owner.Remove(l.PassengerId);
                    break;
                case PassengerBoarded b:
                    Assert.Equal(owner[b.PassengerId], b.ElevatorId);
                    owner.Remove(b.PassengerId);
                    break;
            }
        }

        foreach (var chain in events.OfType<StateChanged>().GroupBy(s => s.ElevatorId))
        {
            var state = ElevatorState.Idle;
            foreach (var sc in chain)
            {
                Assert.Equal(state, sc.From);
                Assert.Equal(TransitionTable.Next(sc.From, sc.Trigger), sc.To);
                state = sc.To;
            }
        }

        var load = new Dictionary<int, int>();
        foreach (var e in events)
        {
            if (e is PassengerBoarded b) Assert.True((load[b.ElevatorId] = load.GetValueOrDefault(b.ElevatorId) + 1) <= capacity);
            if (e is PassengerAlighted a) load[a.ElevatorId]--;
        }
    }

    public static void CheckModes(IReadOnlyList<DomainEvent> events)
    {
        var mode = new Dictionary<int, (bool Requested, bool Parked, bool Emergency, bool Faulty)>();
        (bool, bool, bool, bool) Of(int e) => mode.GetValueOrDefault(e);
        foreach (var e in events)
        {
            switch (e)
            {
                case MaintenanceRequested m: mode[m.ElevatorId] = Of(m.ElevatorId) with { Item1 = true }; break;
                case MaintenanceEntered m: mode[m.ElevatorId] = Of(m.ElevatorId) with { Item2 = true }; break;
                case MaintenanceExited m: mode[m.ElevatorId] = Of(m.ElevatorId) with { Item1 = false, Item2 = false }; break;
                case EmergencyStopped s: mode[s.ElevatorId] = Of(s.ElevatorId) with { Item3 = true }; break;
                case EmergencyCleared s: mode[s.ElevatorId] = Of(s.ElevatorId) with { Item3 = false }; break;
                case FaultDetected f: mode[f.ElevatorId] = Of(f.ElevatorId) with { Item4 = true }; break;
                case FaultRecovered f: mode[f.ElevatorId] = Of(f.ElevatorId) with { Item4 = false }; break;
                case AssignmentAccepted a:
                    Assert.True(Of(a.ElevatorId) == (false, false, false, false), $"E{a.ElevatorId} accepted {a.RequestId} while {Of(a.ElevatorId)}");
                    break;
            }
        }
    }
}
