namespace Elevator.Domain.Control;

/// <summary>Sim-time delays on control messages, to model a dispatcher working from stale snapshots (default: none).</summary>
public sealed record MessageLatency(long SnapshotMs = 0, long DispatchMs = 0)
{
    public static MessageLatency None { get; } = new();
}
