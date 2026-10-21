using Elevator.Domain;
using Elevator.Policy;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Elevator.Simulation;

public sealed record ScenarioLoadResult(Scenario? Scenario, IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Errors.Count == 0;
}

/// <summary>
/// Loads a YAML scenario and validates all of it up front, so a bad file fails with a full list of errors
/// (each pointing at the offending passenger) instead of misbehaving mid-run.
/// </summary>
public static class ScenarioLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static ScenarioLoadResult LoadFile(string path) =>
        File.Exists(path) ? Load(File.ReadAllText(path)) : new(null, [$"scenario file not found: {path}"]);

    public static ScenarioLoadResult Load(string yaml)
    {
        ScenarioDto? dto;
        try
        {
            dto = Yaml.Deserialize<ScenarioDto>(yaml);
        }
        catch (YamlException ex)
        {
            return new(null, [$"invalid YAML at line {ex.Start.Line}: {ex.InnerException?.Message ?? ex.Message}"]);
        }
        if (dto is null) return new(null, ["scenario is empty"]);

        var errors = new List<string>();
        var floors = dto.Floors ?? 10;
        if (floors < 2) errors.Add($"floors must be at least 2 (got {floors})");

        var scheduling = dto.Scheduling ?? "fifo";
        if (!SchedulingPolicies.Names.Contains(scheduling, StringComparer.OrdinalIgnoreCase))
            errors.Add($"unknown scheduling '{scheduling}' (known: {string.Join(", ", SchedulingPolicies.Names)})");

        var maxSimTimeS = dto.MaxSimTimeS ?? 3600;
        if (maxSimTimeS <= 0) errors.Add("maxSimTimeS must be > 0");

        var timing = new ElevatorTiming(
            dto.Timing?.FloorTravelMs ?? ElevatorTiming.Default.FloorTravelMs,
            dto.Timing?.DoorOpeningMs ?? ElevatorTiming.Default.DoorOpeningMs,
            dto.Timing?.DoorDwellMs ?? ElevatorTiming.Default.DoorDwellMs,
            dto.Timing?.DoorClosingMs ?? ElevatorTiming.Default.DoorClosingMs);
        errors.AddRange(timing.Validate().Select(e => $"timing: {e}"));

        var passengers = new List<PassengerSpec>();
        foreach (var (p, i) in (dto.Passengers ?? []).Select((p, i) => (p, i)))
        {
            var where = $"passengers[{i}]";
            if (p.At is not { } at || p.From is not { } from || p.To is not { } to)
            {
                errors.Add($"{where}: 'at', 'from' and 'to' are required");
                continue;
            }
            if (at < 0) errors.Add($"{where}: 'at' must be >= 0");

            Direction? declared = null;
            if (p.Direction is { } text)
            {
                if (Enum.TryParse<Direction>(text, ignoreCase: true, out var parsed)) declared = parsed;
                else errors.Add($"{where}: direction must be 'up' or 'down' (got '{text}')");
            }

            var request = Request.Create(from, to, SimTime.FromSeconds(at), floors, declared);
            if (!request.IsSuccess) errors.Add($"{where}: {request.Error}");
            else passengers.Add(new PassengerSpec(SimTime.FromSeconds(at), from, to));
        }

        PoissonTraffic? traffic = null;
        if (dto.Traffic?.Poisson is { } poisson)
        {
            if (poisson.RatePerMinute is not > 0) errors.Add("traffic.poisson.ratePerMinute must be > 0");
            if (poisson.DurationS is not > 0) errors.Add("traffic.poisson.durationS must be > 0");
            if (poisson.StartS is < 0) errors.Add("traffic.poisson.startS must be >= 0");
            traffic = new PoissonTraffic(poisson.RatePerMinute ?? 0, poisson.DurationS ?? 0, poisson.StartS ?? 0);
        }

        if (errors.Count > 0) return new(null, errors);

        return new(new Scenario(
            dto.Name ?? "unnamed",
            dto.Seed ?? 1,
            floors,
            timing,
            scheduling.ToLowerInvariant(),
            SimTime.FromSeconds(maxSimTimeS),
            passengers,
            traffic), []);
    }

    // YAML shape. Everything nullable so missing keys can be reported instead of silently defaulting to 0.
    private sealed class ScenarioDto
    {
        public string? Name { get; set; }
        public int? Seed { get; set; }
        public int? Floors { get; set; }
        public string? Scheduling { get; set; }
        public double? MaxSimTimeS { get; set; }
        public TimingDto? Timing { get; set; }
        public List<PassengerDto>? Passengers { get; set; }
        public TrafficDto? Traffic { get; set; }
    }

    private sealed class TimingDto
    {
        public int? FloorTravelMs { get; set; }
        public int? DoorOpeningMs { get; set; }
        public int? DoorDwellMs { get; set; }
        public int? DoorClosingMs { get; set; }
    }

    private sealed class PassengerDto
    {
        public double? At { get; set; }
        public int? From { get; set; }
        public int? To { get; set; }
        public string? Direction { get; set; }
    }

    private sealed class TrafficDto
    {
        public PoissonDto? Poisson { get; set; }
    }

    private sealed class PoissonDto
    {
        public double? RatePerMinute { get; set; }
        public double? DurationS { get; set; }
        public double? StartS { get; set; }
    }
}
