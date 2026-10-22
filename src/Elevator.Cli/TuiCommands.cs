using System.ComponentModel;
using System.Globalization;
using Elevator.Domain;
using Elevator.Policy.Dispatch;
using Elevator.Simulation;
using Elevator.Tui.Host;
using Elevator.Tui.Loop;
using Elevator.Tui.State;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Elevator.Cli;

/// <summary>Options shared by <c>tui</c> and <c>replay</c> (ADR-011).</summary>
internal class ViewSettings : CommandSettings
{
    [CommandOption("--speed <SPEED>")]
    [Description("Initial speed: 0.25, 0.5, 1, 2, 5, 10 (default), 50 or max. Change it live with + / -.")]
    public string Speed { get; init; } = "10";

    [CommandOption("--at <SECONDS>")]
    [Description("Start at this sim time (seek before the first frame).")]
    public double? At { get; init; }

    [CommandOption("--paused")]
    [Description("Start paused (space resumes, n single-steps).")]
    public bool Paused { get; init; }

    [CommandOption("--plain")]
    [Description("No full-screen UI: run with virtual frame time (no sleeping) and print the last frame. Implied when output is redirected.")]
    public bool Plain { get; init; }

    [CommandOption("--frames <N>")]
    [Description("Stop after N frames (20 per real second). Default: until q, or to the end in --plain.")]
    public int? Frames { get; init; }

    [CommandOption("--width <COLS>")]
    [Description("Frame width for --plain (default 120).")]
    public int Width { get; init; } = 120;

    [CommandOption("--height <ROWS>")]
    [Description("Frame height for --plain (default 40).")]
    public int Height { get; init; } = 40;

    internal async Task<int> RunAsync(ISession session, CancellationToken cancellationToken)
    {
        Pacer pacer;
        try
        {
            pacer = new Pacer(Pacer.ParseSpeed(Speed));
        }
        catch (FormatException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 2;
        }
        if (Paused) pacer.TogglePause();
        if (At is { } at) session.SeekTo(SimTime.FromSeconds(at));

        if (Plain || !TuiHost.IsInteractiveTerminal)
        {
            var options = new LoopOptions(MaxFrames: Frames, ExitAtEnd: Frames is null);
            var (result, frame) = await TuiHost.RunPlainAsync(session, pacer, options, Width, Height, cancellationToken).ConfigureAwait(false);
            Console.Out.Write(frame);
            if (!frame.EndsWith('\n')) Console.Out.WriteLine();
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{session.Source}: {result.Frames} frames, {result.Ticks} ticks, sim {session.Now.Seconds:0.0} s{(session.AtEnd ? ", finished" : "")}"));
            return 0;
        }

        await TuiHost.RunInteractiveAsync(session, pacer, new LoopOptions(MaxFrames: Frames), cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

internal sealed class TuiCommand : AsyncCommand<TuiCommand.Settings>
{
    public sealed class Settings : ViewSettings
    {
        [CommandArgument(0, "<scenario>")]
        [Description("YAML scenario (path, or a name under scenarios/).")]
        public string Scenario { get; init; } = "";

        [CommandOption("--seed <SEED>")]
        [Description("Override the scenario's seed.")]
        public int? Seed { get; init; }

        [CommandOption("--dispatch <NAME>")]
        [Description("Override the scenario's dispatch policy (cost or nearest).")]
        public string? Dispatch { get; init; }

        [CommandOption("--log <PATH>")]
        [Description("Write the run's JSONL event log here on exit (with DispatchEvaluated lines); replay it with `replay`.")]
        public string? Log { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var path = ScenarioPaths.Resolve(settings.Scenario);
        var loaded = ScenarioLoader.LoadFile(path);
        if (!loaded.IsSuccess)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Invalid scenario {path}:[/]");
            foreach (var error in loaded.Errors) AnsiConsole.MarkupLineInterpolated($"  [red]•[/] {error}");
            return 2;
        }
        var scenario = loaded.Scenario!;
        if (settings.Seed is { } seed) scenario = scenario with { Seed = seed };
        if (settings.Dispatch is { } dispatch)
        {
            if (!DispatchPolicies.IsKnown(dispatch))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Unknown dispatch '{dispatch}'[/] (known: {string.Join(", ", DispatchPolicies.Names)})");
                return 2;
            }
            scenario = scenario with { Dispatch = dispatch.ToLowerInvariant() };
        }

        // Keys m / e / s inject maintenance, emergency stops and stuck faults through the real SimFaultInjector
        // (LiveSession's default), in sim time, so a live run stays deterministic and can be replayed.
        var session = new LiveSession(scenario, faultInjector: SimFaultInjector.Factory);
        var code = await settings.RunAsync(session, cancellationToken).ConfigureAwait(false);
        if (code == 0 && settings.Log is { } logPath)
        {
            JsonlEventLog.WriteFile(logPath, session.Log);
            AnsiConsole.MarkupLineInterpolated($"[grey]Event log: {logPath} ({session.Log.Count} events)[/]");
        }
        return code;
    }
}

internal sealed class ReplayCommand : AsyncCommand<ReplayCommand.Settings>
{
    public sealed class Settings : ViewSettings
    {
        [CommandArgument(0, "<log>")]
        [Description("A JSONL event log (from `run --log`, `tui --log`, or the actor runtime).")]
        public string Log { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Log))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No such log: {settings.Log}[/]");
            return 2;
        }
        return await settings.RunAsync(new ReplaySession(LogReader.ReadFile(settings.Log)), cancellationToken).ConfigureAwait(false);
    }
}
