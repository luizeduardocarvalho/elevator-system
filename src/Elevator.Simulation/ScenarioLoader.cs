using Elevator.Domain;
using Elevator.Domain.Faults;
using Elevator.Policy;
using Elevator.Policy.Dispatch;
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
        if (dto.Floors is not null && dto.Building?.Floors is not null) errors.Add("give 'floors' or 'building.floors', not both");
        var floors = dto.Building?.Floors ?? dto.Floors ?? 10;
        if (floors < 2) errors.Add($"floors must be at least 2 (got {floors})");

        var elevators = dto.Building?.Elevators ?? 1;
        if (elevators < 1) errors.Add($"building.elevators must be at least 1 (got {elevators})");
        var capacity = dto.Building?.Capacity ?? 10;
        if (capacity < 1) errors.Add($"building.capacity must be at least 1 (got {capacity})");
        var bypass = dto.Building?.BypassThreshold ?? 0.8;
        if (bypass is <= 0 or > 1) errors.Add($"building.bypassThreshold must be in (0, 1] (got {bypass})");

        var scheduling = dto.Scheduling ?? "look"; // ADR-004
        if (!SchedulingPolicies.Names.Contains(scheduling, StringComparer.OrdinalIgnoreCase))
            errors.Add($"unknown scheduling '{scheduling}' (known: {string.Join(", ", SchedulingPolicies.Names)})");

        var dispatch = dto.Dispatch ?? "cost"; // ADR-006
        if (!DispatchPolicies.IsKnown(dispatch))
            errors.Add($"unknown dispatch '{dispatch}' (known: {string.Join(", ", DispatchPolicies.Names)})");

        var capacityMode = CapacityMode.NextTrip;
        if (dto.Building?.CapacityMode is { } modeText)
        {
            try { capacityMode = CapacityModes.Parse(modeText); }
            catch (FormatException ex) { errors.Add($"building.capacityMode: {ex.Message}"); }
        }

        var latency = new MessageLatency(dto.Latency?.SnapshotMs ?? 0, dto.Latency?.DispatchMs ?? 0);
        if (latency.SnapshotMs < 0 || latency.DispatchMs < 0) errors.Add("latency values must be >= 0");

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

        TrafficProfile? profile = null;
        if (dto.Traffic?.Profile is { } prof)
        {
            profile = new TrafficProfile(
                (prof.Rate ?? []).Select(r => new RatePoint(r.AtS ?? -1, r.PerMinute ?? -1)).ToList(),
                new TrafficMix(prof.Mix?.FromLobby ?? 0, prof.Mix?.ToLobby ?? 0, prof.Mix?.Interfloor ?? 0),
                prof.Lobby ?? Floors.Lowest);
            errors.AddRange(profile.Validate(floors));
        }

        var faults = new List<FaultCommand>();
        foreach (var (f, i) in (dto.Faults ?? []).Select((f, i) => (f, i)))
        {
            var where = $"faults[{i}]";
            if (f.AtS is not { } atS || f.Elevator is not { } elevator || f.Kind is not { } kindText)
            {
                errors.Add($"{where}: 'atS', 'elevator' and 'kind' are required");
                continue;
            }
            if (atS < 0) errors.Add($"{where}: 'atS' must be >= 0");
            if (elevator < 0 || elevator >= elevators) errors.Add($"{where}: elevator {elevator} does not exist (0..{elevators - 1})");
            if (!FaultKinds.TryParse(kindText, out var kind))
            {
                errors.Add($"{where}: unknown kind '{kindText}' (known: {string.Join(", ", FaultKinds.Names)})");
                continue;
            }
            if (f.DurationS is { } d && (kind != FaultKind.Stuck || d <= 0))
                errors.Add(kind != FaultKind.Stuck ? $"{where}: 'durationS' only applies to kind stuck" : $"{where}: 'durationS' must be > 0");
            faults.Add(new FaultCommand(SimTime.FromSeconds(atS), elevator, kind, f.DurationS is { } ds ? SimTime.FromSeconds(ds).Ms : null));
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
            traffic,
            elevators,
            capacity,
            bypass,
            dispatch.ToLowerInvariant(),
            profile,
            latency,
            capacityMode,
            faults.OrderBy(f => f.At).ToList()), []);
    }

    // YAML shape. Everything nullable so missing keys can be reported instead of silently defaulting to 0.
    private sealed class ScenarioDto
    {
        public string? Name { get; set; }
        public int? Seed { get; set; }
        public int? Floors { get; set; }
        public string? Scheduling { get; set; }
        public string? Dispatch { get; set; }
        public BuildingDto? Building { get; set; }
        public LatencyDto? Latency { get; set; }
        public double? MaxSimTimeS { get; set; }
        public TimingDto? Timing { get; set; }
        public List<PassengerDto>? Passengers { get; set; }
        public TrafficDto? Traffic { get; set; }
        public List<FaultDto>? Faults { get; set; }
    }

    private sealed class FaultDto
    {
        public double? AtS { get; set; }
        public int? Elevator { get; set; }
        public string? Kind { get; set; }
        public double? DurationS { get; set; }
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
        public ProfileDto? Profile { get; set; }
    }

    private sealed class BuildingDto
    {
        public int? Floors { get; set; }
        public int? Elevators { get; set; }
        public int? Capacity { get; set; }
        public double? BypassThreshold { get; set; }
        public string? CapacityMode { get; set; }
    }

    private sealed class LatencyDto
    {
        public long? SnapshotMs { get; set; }
        public long? DispatchMs { get; set; }
    }

    private sealed class ProfileDto
    {
        public int? Lobby { get; set; }
        public MixDto? Mix { get; set; }
        public List<RatePointDto>? Rate { get; set; }
    }

    private sealed class MixDto
    {
        public double? FromLobby { get; set; }
        public double? ToLobby { get; set; }
        public double? Interfloor { get; set; }
    }

    private sealed class RatePointDto
    {
        public double? AtS { get; set; }
        public double? PerMinute { get; set; }
    }

    private sealed class PoissonDto
    {
        public double? RatePerMinute { get; set; }
        public double? DurationS { get; set; }
        public double? StartS { get; set; }
    }
}
