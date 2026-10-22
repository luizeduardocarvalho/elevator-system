using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Faults;
using Elevator.Policy;
using Elevator.Policy.Access;
using Elevator.Policy.Dispatch;
using Elevator.Runtime;
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

        var fleetNames = dto.Building?.Fleet;
        if (fleetNames is not null && dto.Building?.Elevators is { } count && count != fleetNames.Count)
            errors.Add($"building.elevators ({count}) does not match building.fleet ({fleetNames.Count} entries)");
        var elevators = fleetNames?.Count ?? dto.Building?.Elevators ?? 1;
        if (elevators < 1) errors.Add($"building.elevators must be at least 1 (got {elevators})");
        var capacity = dto.Building?.Capacity ?? 10;
        if (capacity < 1) errors.Add($"building.capacity must be at least 1 (got {capacity})");
        var bypass = dto.Building?.BypassThreshold ?? 0.8;
        if (bypass is <= 0 or > 1) errors.Add($"building.bypassThreshold must be in (0, 1] (got {bypass})");

        var dispatch = dto.Dispatch ?? "cost";
        if (!DispatchPolicies.IsKnown(dispatch))
            errors.Add($"unknown dispatch '{dispatch}' (known: {string.Join(", ", DispatchPolicies.Names)})");

        var latency = new MessageLatency(dto.Latency?.SnapshotMs ?? 0, dto.Latency?.DispatchMs ?? 0);
        if (latency.SnapshotMs < 0 || latency.DispatchMs < 0) errors.Add("latency values must be >= 0");

        var maxSimTimeS = dto.MaxSimTimeS ?? 3600;
        if (maxSimTimeS <= 0) errors.Add("maxSimTimeS must be > 0");

        TimeSpan? clockStart = null;
        if (dto.ClockStart is { } clockText)
        {
            if (TimeSpan.TryParseExact(clockText, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var t)) clockStart = t;
            else errors.Add($"clockStart must be a time of day as HH:MM (got '{clockText}')");
        }

        var timing = new ElevatorTiming(
            dto.Timing?.FloorTravelMs ?? ElevatorTiming.Default.FloorTravelMs,
            dto.Timing?.DoorOpeningMs ?? ElevatorTiming.Default.DoorOpeningMs,
            dto.Timing?.DoorDwellMs ?? ElevatorTiming.Default.DoorDwellMs,
            dto.Timing?.DoorClosingMs ?? ElevatorTiming.Default.DoorClosingMs);
        errors.AddRange(timing.Validate().Select(e => $"timing: {e}"));

        var fleet = LoadFleet(dto.Building, floors, timing, capacity, errors);

        var access = LoadAccess(dto.Access, floors, elevators, errors);
        var watchdogMs = RuntimeOptions.DefaultWatchdogTimeoutMs;
        if (dto.Watchdog?.TimeoutS is { } timeoutS)
        {
            if (timeoutS <= 0) errors.Add($"watchdog.timeoutS must be > 0 (got {timeoutS})");
            else watchdogMs = Instant.FromSeconds(timeoutS).Ms;
        }

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

            if (p.AccessLevel is < 0) errors.Add($"{where}: 'accessLevel' must be >= 0");
            var request = Request.Create(from, to, Instant.FromSeconds(at), floors, declared, load: p.Load ?? 1);
            if (!request.IsSuccess) errors.Add($"{where}: {request.Error}");
            else passengers.Add(new PassengerSpec(Instant.FromSeconds(at), from, to) { Vip = p.Vip ?? false, AccessLevel = p.AccessLevel ?? 0, Load = p.Load ?? 1 });
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
                (prof.Rate ?? []).Select(r => new RatePoint(r.AtS ?? -1, r.PerMinute ?? -1,
                    r.Mix is { } m ? new TrafficMix(m.FromLobby ?? 0, m.ToLobby ?? 0, m.Interfloor ?? 0) : null)).ToList(),
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
            faults.Add(new FaultCommand(Instant.FromSeconds(atS), elevator, kind, f.DurationS is { } ds ? Instant.FromSeconds(ds).Ms : null));
        }

        if (errors.Count > 0) return new(null, errors);

        return new(new Scenario(
            dto.Name ?? "unnamed",
            dto.Seed ?? 1,
            floors,
            timing,
            Instant.FromSeconds(maxSimTimeS),
            passengers,
            traffic,
            elevators,
            capacity,
            bypass,
            dispatch.ToLowerInvariant(),
            profile,
            latency,
            faults.OrderBy(f => f.At).ToList()) { Fleet = fleet, Access = access, WatchdogTimeoutMs = watchdogMs, ClockStart = clockStart }, []);
    }

    private static AccessConfig LoadAccess(AccessDto? dto, int floors, int elevators, List<string> errors)
    {
        if (dto is null) return AccessConfig.None;
        var restricted = new Dictionary<int, int>();
        foreach (var (floor, level) in dto.RestrictedFloors ?? [])
        {
            if (Floors.Check(floor, floors) is { } error) errors.Add($"access.restrictedFloors: {error}");
            else if (level < 1) errors.Add($"access.restrictedFloors: floor {floor} needs a level >= 1 (got {level})");
            else restricted[floor] = level;
        }
        var dedicated = dto.Vip?.Dedicated ?? [];
        foreach (var e in dedicated.Where(e => e < 0 || e >= elevators))
            errors.Add($"access.vip.dedicated: elevator {e} does not exist (0..{elevators - 1})");
        return new AccessConfig(restricted, [.. dedicated.Distinct().Order()], dto.Vip?.Priority ?? false);
    }

    private static List<ElevatorType>? LoadFleet(BuildingDto? building, int floors, ElevatorTiming timing, int capacity, List<string> errors)
    {
        var types = new Dictionary<string, ElevatorType>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, t) in building?.Types ?? [])
        {
            var where = $"building.types.{name}";
            var typeTiming = new ElevatorTiming(
                t?.FloorTravelMs ?? timing.FloorTravelMs, t?.DoorOpeningMs ?? timing.DoorOpeningMs,
                t?.DoorDwellMs ?? timing.DoorDwellMs, t?.DoorClosingMs ?? timing.DoorClosingMs);
            errors.AddRange(typeTiming.Validate().Select(e => $"{where}: {e}"));
            var typeCapacity = t?.Capacity ?? capacity;
            if (typeCapacity < 1) errors.Add($"{where}: capacity must be at least 1 (got {typeCapacity})");
            var served = FloorMask.All;
            if (t?.ServedFloors is { } list)
            {
                var parsed = FloorMask.Parse(list, floors);
                if (parsed.IsSuccess) served = parsed.Value;
                else errors.Add($"{where}.servedFloors: {parsed.Error}");
            }
            types[name] = new ElevatorType(name, typeTiming, typeCapacity, served);
        }

        if (building?.Fleet is not { } names)
        {
            if (types.Count > 0) errors.Add("building.types is given but building.fleet is missing");
            return null;
        }
        var fleet = new List<ElevatorType>();
        foreach (var (name, i) in names.Select((n, i) => (n, i)))
        {
            if (types.TryGetValue(name, out var type)) fleet.Add(type);
            else errors.Add($"building.fleet[{i}]: unknown type '{name}' (known: {string.Join(", ", types.Keys)})");
        }
        return fleet;
    }

    private sealed class ScenarioDto
    {
        public string? Name { get; set; }
        public int? Seed { get; set; }
        public int? Floors { get; set; }
        public string? Dispatch { get; set; }
        public BuildingDto? Building { get; set; }
        public LatencyDto? Latency { get; set; }
        public double? MaxSimTimeS { get; set; }
        public string? ClockStart { get; set; }
        public TimingDto? Timing { get; set; }
        public List<PassengerDto>? Passengers { get; set; }
        public TrafficDto? Traffic { get; set; }
        public List<FaultDto>? Faults { get; set; }
        public AccessDto? Access { get; set; }
        public WatchdogDto? Watchdog { get; set; }
    }

    private sealed class WatchdogDto
    {
        public double? TimeoutS { get; set; }
    }

    private sealed class AccessDto
    {
        public Dictionary<int, int>? RestrictedFloors { get; set; }
        public VipDto? Vip { get; set; }
    }

    private sealed class VipDto
    {
        public bool? Priority { get; set; }
        public List<int>? Dedicated { get; set; }
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
        public bool? Vip { get; set; }
        public int? AccessLevel { get; set; }
        public int? Load { get; set; }
    }

    private sealed class ElevatorTypeDto
    {
        public int? FloorTravelMs { get; set; }
        public int? DoorOpeningMs { get; set; }
        public int? DoorDwellMs { get; set; }
        public int? DoorClosingMs { get; set; }
        public int? Capacity { get; set; }
        public List<string>? ServedFloors { get; set; }
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
        public Dictionary<string, ElevatorTypeDto?>? Types { get; set; }
        public List<string>? Fleet { get; set; }
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
        public MixDto? Mix { get; set; }
    }

    private sealed class PoissonDto
    {
        public double? RatePerMinute { get; set; }
        public double? DurationS { get; set; }
        public double? StartS { get; set; }
    }
}
