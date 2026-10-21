using Elevator.Tui.Loop;
using Elevator.Tui.View;

namespace Elevator.Tui.Tests;

/// <summary>A frame clock that never sleeps: a delay just moves time forward (no Thread.Sleep in tests).</summary>
internal sealed class FakeClock : IFrameClock
{
    public TimeSpan Now { get; set; }

    public TimeSpan Elapsed => Now;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Now += delay;
        return Task.CompletedTask;
    }
}

/// <summary>Keys pressed at given frame numbers (counted by TryRead calls that return false, i.e. once per frame).</summary>
internal sealed class ScriptedKeys(params (int Frame, TuiKey Key)[] script) : IKeySource
{
    private readonly Queue<(int Frame, TuiKey Key)> _script = new(script.OrderBy(s => s.Frame));
    private int _frame;

    public bool TryRead(out TuiKey key)
    {
        if (_script.TryPeek(out var next) && next.Frame <= _frame)
        {
            key = _script.Dequeue().Key;
            return true;
        }
        _frame++;
        key = default;
        return false;
    }
}

internal sealed class CapturingPresenter(int width = 120, int height = 40, Func<int, bool>? capture = null) : IPresenter
{
    private int _frame;

    public (int Width, int Height) Size => (width, height);

    public List<(int Frame, ScreenModel Model)> Captured { get; } = [];

    public ScreenModel? Last { get; private set; }

    public void Present(ScreenModel model)
    {
        Last = model;
        if (capture?.Invoke(_frame) == true) Captured.Add((_frame, model));
        _frame++;
    }
}
