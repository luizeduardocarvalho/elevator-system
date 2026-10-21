namespace Elevator.Domain;

/// <summary>A hall call: someone on <paramref name="Floor"/> pressed the <paramref name="Direction"/> button.</summary>
public sealed record HallCall(int Floor, Direction Direction, SimTime RegisteredAt);
