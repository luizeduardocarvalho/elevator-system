using Elevator.Tui.View;

namespace Elevator.Tui.Loop;

/// <summary>Real time for the loop: a Stopwatch in the terminal, a fake in tests (no sleeping, ADR-011).</summary>
public interface IFrameClock
{
    TimeSpan Elapsed { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IKeySource
{
    bool TryRead(out TuiKey key);
}

/// <summary>Shows a frame. The only thing it receives is the immutable ScreenModel.</summary>
public interface IPresenter
{
    (int Width, int Height) Size { get; }

    void Present(ScreenModel model);
}

/// <param name="MaxFrames">Stop after this many frames (CI smoke runs); null = until q.</param>
/// <param name="ExitAtEnd">Stop once the session has nothing more to show.</param>
public sealed record LoopOptions(int? MaxFrames = null, bool ExitAtEnd = false, double FramePeriodMs = 50, double FrameBudgetMs = 30);

public sealed record LoopResult(int Frames, long Ticks, bool Quit);

/// <summary>
/// The frame loop (ADR-011), about 20 fps. Each frame: read keys; ask the <see cref="Pacer"/> how many fixed sim
/// ticks are due for the real time that passed; run them (stopping early, and dropping the rest, if the frame's work
/// budget runs out); build the ScreenModel from the folded state; present it; sleep to the next frame.
///
/// The renderer is downstream of all of this and never calls back: the sim advances only here, only in ticks.
/// </summary>
public sealed class TuiLoop(ISession session, Pacer pacer, IFrameClock clock, IKeySource keys, IPresenter presenter, LoopOptions? options = null)
{
    private readonly LoopOptions _options = options ?? new LoopOptions();
    private readonly SpeedMeter _meter = new();
    private string? _message;

    public ISession Session => session;

    public int Selected { get; private set; }

    public long Ticks { get; private set; }

    public async Task<LoopResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var frames = 0;
        var last = clock.Elapsed;
        while (!cancellationToken.IsCancellationRequested)
        {
            var frameStart = clock.Elapsed;
            var realDelta = (frameStart - last).TotalMilliseconds;
            last = frameStart;

            if (!HandleKeys()) return new LoopResult(frames, Ticks, Quit: true);

            var due = pacer.TicksDue(realDelta);
            for (var i = 0; i < due && !session.AtEnd; i++)
            {
                session.Step();
                Ticks++;
                if ((clock.Elapsed - frameStart).TotalMilliseconds > _options.FrameBudgetMs && i + 1 < due)
                {
                    pacer.BudgetExceeded();
                    break;
                }
            }
            _meter.Record(frameStart, session.Now.Ms);

            presenter.Present(BuildScreen());
            frames++;

            if (_options.MaxFrames is { } max && frames >= max) break;
            if (_options.ExitAtEnd && session.AtEnd) break;

            var wait = frameStart + TimeSpan.FromMilliseconds(_options.FramePeriodMs) - clock.Elapsed;
            await clock.DelayAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
        return new LoopResult(frames, Ticks, Quit: false);
    }

    public ScreenModel BuildScreen()
    {
        var (width, height) = presenter.Size;
        var status = new StatusInfo(session.Source, pacer.SpeedLabel, _meter.Achieved, pacer.Paused, _message, session.ReadOnly);
        return ViewModel.Build(session.State, new ViewOptions(width, height, Selected, status));
    }

    /// <summary>Applies pending keys; false on quit.</summary>
    private bool HandleKeys()
    {
        while (keys.TryRead(out var key))
        {
            var count = Math.Max(1, session.State.Elevators.Count);
            switch (key)
            {
                case TuiKey.Quit: return false;
                case TuiKey.Pause: pacer.TogglePause(); break;
                case TuiKey.Step: pacer.StepOnce(); break;
                case TuiKey.Faster: pacer.Faster(); break;
                case TuiKey.Slower: pacer.Slower(); break;
                case TuiKey.SelectPrevious: Selected = (Selected - 1 + count) % count; break;
                case TuiKey.SelectNext: Selected = (Selected + 1) % count; break;
                case TuiKey.Maintenance or TuiKey.Emergency or TuiKey.Stuck:
                    _message = session.ReadOnly ? "read-only" : session.Command(key, Selected);
                    break;
                case TuiKey.SeekBack: Seek(session.Now.AddMs(-60_000)); break;
                case TuiKey.SeekForward: Seek(session.Now.AddMs(60_000)); break;
                case TuiKey.Restart: Seek(Domain.SimTime.Zero); break;
            }
        }
        return true;
    }

    private void Seek(Domain.SimTime target)
    {
        session.SeekTo(target.Ms < 0 ? Domain.SimTime.Zero : target);
        _meter.Reset();
        _message = $"seek → {ViewModel.Clock(session.Now)}";
    }
}
