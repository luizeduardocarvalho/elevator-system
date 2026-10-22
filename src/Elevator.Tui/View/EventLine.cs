using System.Globalization;
using System.Text.Json;
using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Tui.State;

namespace Elevator.Tui.View;

/// <summary>
/// One event-tail line per log item. Known events get a short, readable form; anything else (an unknown type from a
/// newer log, or an event this switch has no case for) gets a generic <c>key=value</c> line built from its JSON, so
/// a new event type can never crash the TUI.
/// </summary>
public static class EventLine
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static TailLine Format(LogItem item)
    {
        var time = item.At.Seconds.ToString("0.0", Inv);
        if (item.Unknown is { } u) return Generic(time, u.Type, u.Fields);

        var e = item.Known!;
        if (e is DispatchEvaluated d) return Assign(time, d);

        (string Kind, string Text, Tone Tone)? line = e switch
        {
            RunStarted s => ("SIM", $"'{s.Scenario}' seed {s.Seed}: {s.Floors} floors, {s.Elevators} elevators, {s.Passengers} passengers", Tone.Accent),
            RunEnded s => ("SIM", $"ended: {s.Delivered} delivered, {s.Unserved} unserved", Tone.Accent),
            PassengerArrived p => ("ARRIVE", $"P{p.PassengerId} {p.From}→{p.To}", Tone.Normal),
            PassengerBoarded p => ("BOARD", $"P{p.PassengerId} into E{p.ElevatorId} @{p.Floor}", Tone.Good),
            PassengerAlighted p => ("ALIGHT", $"P{p.PassengerId} out of E{p.ElevatorId} @{p.Floor}", Tone.Good),
            PassengerLeftBehind p => ("LEFT", $"P{p.PassengerId} left behind by full E{p.ElevatorId} @{p.Floor}", Tone.Warn),
            HallCallRegistered h => ("HALL", $"{h.Floor}{Arrow(h.Direction)} lit for E{h.ElevatorId}", Tone.Dim),
            HallCallAnswered h => ("ANSWER", $"E{h.ElevatorId} answers {h.Floor}{Arrow(h.Direction)}", Tone.Dim),
            CarCallRegistered c => ("CAR", $"E{c.ElevatorId} → {c.Floor}", Tone.Dim),
            RequestRejected r => ("INVALID", $"{r.Kind} floor {r.Floor}: {r.Reason}", Tone.Warn),
            StateChanged s => ("STATE", $"E{s.ElevatorId} {Spec(s.From)} → {Spec(s.To)} @{s.Floor}", Tone.Dim),
            DoorsOpened o => ("DOORS", $"E{o.ElevatorId} open @{o.Floor}, lantern {(o.Lantern is { } l ? Arrow(l) : "off")}", Tone.Dim),
            RequestSubmitted r => ("SUBMIT", $"R{r.RequestId} {r.From}→{r.To}", Tone.Normal),
            AssignmentProposed a => ("PROPOSE", string.Create(Inv, $"R{a.RequestId} → E{a.ElevatorId} cost {a.Cost:0.0}{(a.Forced ? " (forced)" : "")}"), Tone.Accent),
            AssignmentAccepted a => ("ACCEPT", $"E{a.ElevatorId} takes R{a.RequestId}", Tone.Normal),
            AssignmentRejected a => ("REJECT", $"E{a.ElevatorId} rejects R{a.RequestId} ({Spec(a.Reason)})", Tone.Warn),
            AssignmentReleased a => ("RELEASE", $"E{a.ElevatorId} hands back R{a.RequestId} ({Spec(a.Reason)})", Tone.Warn),
            DispatchDeferred d2 => ("DEFER", $"R{d2.RequestId}: no elevator eligible yet", Tone.Warn),
            DuplicateSubmission d2 => ("DUP", $"R{d2.RequestId} already in flight", Tone.Dim),
            OverloadModeChanged o => ("OVERLOAD", o.Active ? $"{o.InFlight} in flight: fallback dispatch" : $"over ({o.InFlight} in flight)", o.Active ? Tone.Bad : Tone.Good),
            MaintenanceRequested m => ("MAINT", $"E{m.ElevatorId} draining", Tone.Warn),
            MaintenanceEntered m => ("MAINT", $"E{m.ElevatorId} parked @{m.Floor}", Tone.Warn),
            MaintenanceExited m => ("MAINT", $"E{m.ElevatorId} back in service", Tone.Good),
            EmergencyStopped es => ("EMERGENCY", $"E{es.ElevatorId} stopped @{es.Floor}", Tone.Bad),
            EmergencyCleared ec => ("EMERGENCY", $"E{ec.ElevatorId} cleared", Tone.Good),
            FaultDetected f => ("FAULT", string.Create(Inv, $"E{f.ElevatorId} silent {f.SilentForMs / 1000.0:0} s @{f.Floor}: calls reassigned"), Tone.Bad),
            FaultRecovered f => ("FAULT", $"E{f.ElevatorId} recovered", Tone.Good),
            AccessDenied a => ("DENIED", $"R{a.RequestId} {(a.ElevatorId is { } id ? $"E{id}" : "all elevators")}: {a.Policy}: {a.Reason}", Tone.Warn),
            FaultCommandApplied c => ("COMMAND", $"E{c.ElevatorId} {Spec(c.Kind)}{(c.DurationMs is { } ms ? string.Create(Inv, $" {ms / 1000.0:0} s") : "")}", Tone.Accent),
            _ => null,
        };
        return line is { } l2 ? new TailLine(time, l2.Kind, l2.Text, l2.Tone, []) : Generic(time, e.GetType().Name, LogReader.FieldsOf(e));
    }

    private static TailLine Assign(string time, DispatchEvaluated d)
    {
        var costs = d.Candidates.Select(c => new CostCell(
            $"E{c.ElevatorId}",
            c.Cost is { } cost ? cost.ToString("0.0", Inv) : "∞",
            c.ElevatorId == d.WinnerId)).ToList();
        return new TailLine(time, "ASSIGN", $"R{d.RequestId} → {(d.WinnerId is { } w ? $"E{w}" : "none")}", Tone.Accent, costs);
    }

    private static TailLine Generic(string time, string type, IReadOnlyList<KeyValuePair<string, string>> fields) =>
        new(time, type, string.Join(' ', fields.Select(f => $"{f.Key}={f.Value}")), Tone.Dim, []);

    private static string Arrow(Direction d) => d == Direction.Up ? "▲" : "▼";

    private static string Spec<T>(T value) where T : Enum => JsonNamingPolicy.SnakeCaseUpper.ConvertName(value.ToString());
}
