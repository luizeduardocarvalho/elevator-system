using System.Diagnostics;
using Elevator.Tui.Loop;
using Elevator.Tui.Render;
using Elevator.Tui.View;
using Spectre.Console;

namespace Elevator.Tui.Host;

/// <summary>Real time for the interactive loop.</summary>
public sealed class StopwatchFrameClock : IFrameClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _watch.Elapsed;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Virtual real time: every frame "takes" exactly the frame period and nothing sleeps. Used by <c>--plain</c> (CI
/// smoke runs, non-terminal output): N frames at a given speed advance the sim exactly as N interactive frames
/// would, instantly and deterministically.
/// </summary>
public sealed class VirtualFrameClock : IFrameClock
{
    public TimeSpan Elapsed { get; private set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Elapsed += delay;
        return Task.CompletedTask;
    }
}

/// <summary>Non-blocking keyboard polling (<see cref="Console.KeyAvailable"/>); silent when input is redirected.</summary>
public sealed class ConsoleKeySource : IKeySource
{
    public bool TryRead(out TuiKey key)
    {
        key = default;
        if (Console.IsInputRedirected) return false;
        while (Console.KeyAvailable)
        {
            if (KeyMap.From(Console.ReadKey(intercept: true)) is { } mapped)
            {
                key = mapped;
                return true;
            }
        }
        return false;
    }
}

public sealed class NoKeys : IKeySource
{
    public bool TryRead(out TuiKey key)
    {
        key = default;
        return false;
    }
}

/// <summary>Keeps the last frame (plain mode prints it once at the end).</summary>
public sealed class LastFramePresenter(int width, int height) : IPresenter
{
    public (int Width, int Height) Size => (width, height);

    public ScreenModel? Last { get; private set; }

    public void Present(ScreenModel model) => Last = model;
}

/// <summary>Spectre Live: replaces the whole screen with each frame's renderable.</summary>
internal sealed class LivePresenter(LiveDisplayContext context) : IPresenter
{
    public (int Width, int Height) Size
    {
        get
        {
            try
            {
                // One row less than the terminal so the live region never scrolls it.
                return (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight - 1));
            }
            catch (IOException)
            {
                return (120, 40);
            }
        }
    }

    public void Present(ScreenModel model)
    {
        context.UpdateTarget(SpectreRenderer.Render(model));
        context.Refresh();
    }
}

/// <summary>Runs a session in the terminal (interactive) or headless (plain).</summary>
public static class TuiHost
{
    /// <summary>
    /// Full-screen interactive run: alternate screen, hidden cursor, keys polled each frame. The terminal is restored
    /// on quit, on Ctrl+C (cancels the loop instead of killing the process) and on exceptions.
    /// </summary>
    public static async Task<LoopResult> RunInteractiveAsync(ISession session, Pacer pacer, LoopOptions options, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cts.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        var console = AnsiConsole.Console;
        LoopResult? result = null;
        try
        {
            console.Write(new ControlCode("\u001b[?1049h\u001b[H")); // alternate screen, cursor home
            console.Cursor.Hide();
            await console.Live(new Text(""))
                .AutoClear(true)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async ctx =>
                {
                    var loop = new TuiLoop(session, pacer, new StopwatchFrameClock(), new ConsoleKeySource(), new LivePresenter(ctx), options);
                    try
                    {
                        result = await loop.RunAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        result = new LoopResult(0, loop.Ticks, Quit: true);
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            console.Cursor.Show();
            console.Write(new ControlCode("\u001b[?1049l")); // back to the normal screen
            Console.CancelKeyPress -= OnCancel;
        }
        return result ?? new LoopResult(0, 0, Quit: true);
    }

    /// <summary>
    /// Headless run with virtual frame time (CI, redirected output): runs the loop for <see cref="LoopOptions.MaxFrames"/>
    /// frames (or to the end) and returns the last frame rendered as plain text at <paramref name="width"/>×<paramref name="height"/>.
    /// </summary>
    public static async Task<(LoopResult Result, string Frame)> RunPlainAsync(ISession session, Pacer pacer, LoopOptions options, int width, int height,
        CancellationToken cancellationToken)
    {
        var presenter = new LastFramePresenter(width, height);
        var loop = new TuiLoop(session, pacer, new VirtualFrameClock(), new NoKeys(), presenter, options);
        var result = await loop.RunAsync(cancellationToken).ConfigureAwait(false);
        return (result, SpectreRenderer.RenderToString(loop.BuildScreen(), width, height));
    }

    /// <summary>True when there is a terminal to draw on and read keys from.</summary>
    public static bool IsInteractiveTerminal => !Console.IsOutputRedirected && !Console.IsInputRedirected;
}
