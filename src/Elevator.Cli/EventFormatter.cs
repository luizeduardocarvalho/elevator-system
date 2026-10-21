using System.Text.Json;
using Elevator.Domain.Events;

namespace Elevator.Cli;

/// <summary>Human-readable one-liners for the event log (the spec's "simple logging of elevator actions").</summary>
internal static class EventFormatter
{
    public static string Format(DomainEvent e) => $"[{e.At.Seconds,8:0.0}s] " + e switch
    {
        SimulationStarted s => $"simulation '{s.Scenario}' started: {s.Floors} floors, {s.Passengers} passengers, scheduling {s.Scheduling}, seed {s.Seed}",
        SimulationEnded s => $"simulation ended: {s.Delivered} delivered, {s.Unserved} unserved",
        PassengerArrived p => $"P{p.PassengerId} arrives at floor {p.From}, wants {p.To}",
        PassengerBoarded p => $"P{p.PassengerId} boards E{p.ElevatorId} at floor {p.Floor}",
        PassengerAlighted p => $"P{p.PassengerId} gets off E{p.ElevatorId} at floor {p.Floor}",
        HallCallRegistered h => $"hall call {h.Floor}{Arrow(h.Direction)} registered",
        HallCallAnswered h => $"E{h.ElevatorId} answers hall call {h.Floor}{Arrow(h.Direction)}",
        CarCallRegistered c => $"E{c.ElevatorId} car call to floor {c.Floor}",
        RequestRejected r => $"REJECTED {r.Kind} floor {r.Floor}: {r.Reason}",
        StateChanged s => $"E{s.ElevatorId} {Spec(s.From)} -> {Spec(s.To)} at floor {s.Floor} ({Spec(s.Trigger)})",
        FloorReached f => $"E{f.ElevatorId} reaches floor {f.Floor}",
        DoorsOpened d => $"E{d.ElevatorId} doors open at floor {d.Floor}, lantern {(d.Lantern is { } l ? Spec(l) : "off")}",
        RequestSubmitted r => $"request {r.RequestId} ({r.From}->{r.To}) submitted",
        AssignmentProposed a => $"dispatcher proposes E{a.ElevatorId} for request {a.RequestId} (cost {a.Cost}{(a.Forced ? ", forced" : "")})",
        AssignmentAccepted a => $"E{a.ElevatorId} accepts request {a.RequestId}",
        AssignmentRejected a => $"E{a.ElevatorId} rejects request {a.RequestId} ({Spec(a.Reason)})",
        AssignmentReleased a => $"E{a.ElevatorId} releases request {a.RequestId} ({Spec(a.Reason)})",
        DispatchDeferred d => $"no elevator eligible for request {d.RequestId}; waiting",
        DuplicateSubmission d => $"duplicate submission of request {d.RequestId} ignored",
        PassengerLeftBehind p => $"P{p.PassengerId} left behind by full E{p.ElevatorId} at floor {p.Floor}",
        _ => e.ToString(),
    };

    private static string Arrow(Domain.Direction d) => d == Domain.Direction.Up ? "↑" : "↓";

    private static string Spec<T>(T value) where T : Enum => JsonNamingPolicy.SnakeCaseUpper.ConvertName(value.ToString());
}
