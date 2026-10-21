using Elevator.Domain;
using Elevator.Tui.State;

namespace Elevator.Tui.Loop;

/// <summary>
/// The replay view's source: a cursor over a recorded log, folded with the same <see cref="BuildingState"/> as the
/// live view, so a replay shows what the live run showed at the same sim time.
///
/// <b>Seek backward = re-fold from the start</b>, not re-simulate (ADR-011): the log is the source of truth
/// (ADR-007); it may come from the non-deterministic actor runtime or an older build; and it does not carry the
/// scenario YAML. Re-folding ~10k events takes milliseconds.
/// </summary>
public sealed class ReplaySession : ISession
{
    private readonly IReadOnlyList<LogItem> _items;
    private int _next;

    public ReplaySession(IReadOnlyList<LogItem> items)
    {
        _items = items;
        End = items.Count == 0 ? SimTime.Zero : items.Max(i => i.At);
        State = new BuildingState();
        FoldTo(SimTime.Zero);
    }

    public string Source => "REPLAY";

    public BuildingState State { get; private set; }

    public SimTime Now { get; private set; }

    /// <summary>The last event's time: the cursor stops there.</summary>
    public SimTime End { get; }

    public bool AtEnd => _next >= _items.Count && Now >= End;

    public bool ReadOnly => true;

    public void Step() => FoldTo(SimTime.Min(Now.AddMs(Pacer.TickMs), End));

    public void SeekTo(SimTime target)
    {
        target = SimTime.Min(target < SimTime.Zero ? SimTime.Zero : target, End);
        if (target < Now)
        {
            State = new BuildingState();
            _next = 0;
            Now = SimTime.Zero;
        }
        FoldTo(target);
    }

    public string Command(TuiKey key, int elevator) => "replay is read-only: commands are already in the log";

    /// <summary>
    /// Folds every item up to <paramref name="target"/> in file order. Logs from the actor runtime can be a few ms
    /// out of sim-time order (ADR-007); such an item is folded with the earliest cursor position that covers its
    /// predecessors, and the clock never runs backwards.
    /// </summary>
    private void FoldTo(SimTime target)
    {
        while (_next < _items.Count && _items[_next].At <= target) State.Apply(_items[_next++]);
        Now = target;
        State.AdvanceTo(target);
    }
}
