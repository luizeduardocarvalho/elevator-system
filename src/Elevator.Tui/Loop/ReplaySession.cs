using Elevator.Domain;
using Elevator.Tui.State;

namespace Elevator.Tui.Loop;

/// <summary>
/// The replay view's source: a cursor over a recorded log, folded with the same <see cref="BuildingState"/> as the
/// live view, so a replay shows what the live run showed at the same sim time.
/// <b>Seek backward = re-fold from the start</b>, not re-simulate (ADR-011).
/// </summary>
public sealed class ReplaySession : ISession
{
    private readonly IReadOnlyList<LogItem> _items;
    private int _next;

    public ReplaySession(IReadOnlyList<LogItem> items)
    {
        _items = items;
        End = items.Count == 0 ? Instant.Zero : items.Max(i => i.At);
        State = new BuildingState();
        FoldTo(Instant.Zero);
    }

    public string Source => "REPLAY";

    public BuildingState State { get; private set; }

    public Instant Now { get; private set; }

    /// <summary>The last event's time: the cursor stops there.</summary>
    public Instant End { get; }

    public bool AtEnd => _next >= _items.Count && Now >= End;

    public bool ReadOnly => true;

    public void Step() => FoldTo(Instant.Min(Now.AddMs(Pacer.TickMs), End));

    public void SeekTo(Instant target)
    {
        target = Instant.Min(target < Instant.Zero ? Instant.Zero : target, End);
        if (target < Now)
        {
            State = new BuildingState();
            _next = 0;
            Now = Instant.Zero;
        }
        FoldTo(target);
    }

    public string Command(TuiKey key, int elevator) => "replay is read-only: commands are already in the log";

    private void FoldTo(Instant target)
    {
        while (_next < _items.Count && _items[_next].At <= target) State.Apply(_items[_next++]);
        Now = target;
        State.AdvanceTo(target);
    }
}
