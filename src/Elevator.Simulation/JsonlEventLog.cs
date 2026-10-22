using System.Text.Json;
using System.Text.Json.Serialization;
using Elevator.Domain;
using Elevator.Domain.Events;

namespace Elevator.Simulation;

/// <summary>
/// The event log: one JSON object per line, <c>{"seq":n,"type":"...","t":ms,...}</c>. Enums use the spec's
/// spelling (MOVING_UP, DOOR_OPEN...).
/// </summary>
public static class JsonlEventLog
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowOutOfOrderMetadataProperties = true, // "type" follows "seq"
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper), new SimTimeMsConverter() },
    };

    public static string Serialize(DomainEvent e, long seq)
    {
        var json = JsonSerializer.Serialize(e, Options);
        return $"{{\"seq\":{seq},{json[1..]}";
    }

    public static DomainEvent Deserialize(string line) =>
        JsonSerializer.Deserialize<DomainEvent>(line, Options) ?? throw new JsonException("null event");

    public static void Write(TextWriter writer, IEnumerable<DomainEvent> events)
    {
        long seq = 0;
        foreach (var e in events) writer.WriteLine(Serialize(e, seq++));
    }

    public static void WriteFile(string path, IEnumerable<DomainEvent> events)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        using var writer = new StreamWriter(path);
        writer.NewLine = "\n";
        Write(writer, events);
    }

    public static IEnumerable<DomainEvent> Read(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) yield return Deserialize(line);
    }

    private sealed class SimTimeMsConverter : JsonConverter<SimTime>
    {
        public override SimTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, SimTime value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Ms);
    }
}
