namespace Elevator.Domain;

/// <summary>
/// Durations of the elevator's timed phases, in sim milliseconds. Constant speed per floor: no acceleration
/// model in v1 (ADR-003).
/// </summary>
public sealed record ElevatorTiming(
    int FloorTravelMs = 2000,
    int DoorOpeningMs = 1000,
    int DoorDwellMs = 3000,
    int DoorClosingMs = 1000)
{
    public static ElevatorTiming Default { get; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (FloorTravelMs <= 0) errors.Add("floorTravelMs must be > 0");
        if (DoorOpeningMs <= 0) errors.Add("doorOpeningMs must be > 0");
        if (DoorDwellMs <= 0) errors.Add("doorDwellMs must be > 0");
        if (DoorClosingMs <= 0) errors.Add("doorClosingMs must be > 0");
        return errors;
    }
}
