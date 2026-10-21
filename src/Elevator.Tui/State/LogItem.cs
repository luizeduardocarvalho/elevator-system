using System.Globalization;
using System.Text.Json;
using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Simulation;

namespace Elevator.Tui.State;

/// <summary>
/// An event-log line whose type this build does not know (a newer log, or a hand-edited one), kept as its type name
/// and flat <c>key=value</c> fields so the event tail can still show it. Not a <see cref="DomainEvent"/>: the TUI
/// adds no event types (shared v3 contract).
/// </summary>
public sealed record UnknownEvent(SimTime At, string Type, IReadOnlyList<KeyValuePair<string, string>> Fields);

/// <summary>One line of the log as the TUI folds it: a known event or an unknown one (exactly one is set).</summary>
public readonly record struct LogItem(DomainEvent? Known, UnknownEvent? Unknown)
{
    public SimTime At => Known?.At ?? Unknown!.At;

    public static implicit operator LogItem(DomainEvent e) => new(e, null);

    public static implicit operator LogItem(UnknownEvent e) => new(null, e);
}

/// <summary>
/// Reads a JSONL event log for <c>replay</c>. Never throws on content: a line of an unknown type becomes an
/// <see cref="UnknownEvent"/>, and a line that is not even a JSON object becomes an "UNREADABLE" one at the
/// previous line's time (the tail shows it; the fold ignores it).
/// </summary>
public static class LogReader
{
    public static IReadOnlyList<LogItem> ReadFile(string path)
    {
        using var reader = new StreamReader(path);
        return Read(reader);
    }

    public static IReadOnlyList<LogItem> Read(TextReader reader)
    {
        var items = new List<LogItem>();
        var last = SimTime.Zero;
        var lineNo = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Length == 0) continue;
            var item = Parse(line, last, lineNo);
            last = item.At;
            items.Add(item);
        }
        return items;
    }

    public static LogItem Parse(string line, SimTime previous, int lineNo = 0)
    {
        try
        {
            return JsonlEventLog.Deserialize(line);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return ParseUnknown(line, previous, lineNo);
        }
    }

    private static UnknownEvent ParseUnknown(string line, SimTime previous, int lineNo)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Unreadable(previous, lineNo);
            var root = doc.RootElement;
            var at = root.TryGetProperty("t", out var t) && t.TryGetInt64(out var ms) ? new SimTime(ms) : previous;
            var type = root.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString()! : "UNKNOWN";
            return new UnknownEvent(at, type, Flatten(root));
        }
        catch (JsonException)
        {
            return Unreadable(previous, lineNo);
        }
    }

    private static UnknownEvent Unreadable(SimTime at, int lineNo) =>
        new(at, "UNREADABLE", [new("line", lineNo.ToString(CultureInfo.InvariantCulture))]);

    /// <summary>
    /// The fields of any event as <c>key=value</c> pairs, from its JSONL form (same spelling as the log). Used for
    /// the generic tail line, so a new event type renders without code changes.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FieldsOf(DomainEvent e)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(e, JsonlEventLog.Options));
        return Flatten(doc.RootElement);
    }

    private static List<KeyValuePair<string, string>> Flatten(JsonElement root)
    {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name is "type" or "t" or "seq") continue;
            var value = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString()!,
                JsonValueKind.Null => "null",
                _ => p.Value.GetRawText(),
            };
            fields.Add(new(p.Name, value));
        }
        return fields;
    }
}
