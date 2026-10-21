using Elevator.Domain;
using Elevator.Tui.State;

namespace Elevator.Tui.Loop;

/// <summary>Keys the loop understands (mapped from the console by <see cref="KeyMap"/>).</summary>
public enum TuiKey { Pause, Step, Faster, Slower, SelectPrevious, SelectNext, Maintenance, Emergency, Stuck, SeekBack, SeekForward, Restart, Quit }

/// <summary>
/// What the loop drives: a live engine or a replayed log. Both expose the same folded <see cref="BuildingState"/>;
/// the loop only steps in fixed ticks, seeks, and forwards operator keys.
/// </summary>
public interface ISession
{
    /// <summary>"LIVE" or "REPLAY".</summary>
    string Source { get; }

    BuildingState State { get; }

    SimTime Now { get; }

    /// <summary>Nothing more will happen (the run finished, or the log is exhausted).</summary>
    bool AtEnd { get; }

    /// <summary>Operator commands are not possible (replay).</summary>
    bool ReadOnly { get; }

    /// <summary>Advances one fixed tick (<see cref="Pacer.TickMs"/>).</summary>
    void Step();

    /// <summary>Jumps to <paramref name="target"/>: forward by running ahead, backward by rebuilding from the start.</summary>
    void SeekTo(SimTime target);

    /// <summary>m / e / s on elevator <paramref name="elevator"/>; returns the status-bar message.</summary>
    string Command(TuiKey key, int elevator);
}

public static class KeyMap
{
    public static TuiKey? From(ConsoleKeyInfo key) => key.Key switch
    {
        ConsoleKey.Spacebar => TuiKey.Pause,
        ConsoleKey.N => TuiKey.Step,
        ConsoleKey.Add or ConsoleKey.OemPlus => TuiKey.Faster,
        ConsoleKey.Subtract or ConsoleKey.OemMinus => TuiKey.Slower,
        ConsoleKey.LeftArrow => TuiKey.SelectPrevious,
        ConsoleKey.RightArrow => TuiKey.SelectNext,
        ConsoleKey.M => TuiKey.Maintenance,
        ConsoleKey.E => TuiKey.Emergency,
        ConsoleKey.S => TuiKey.Stuck,
        ConsoleKey.Oem4 => TuiKey.SeekBack,    // [
        ConsoleKey.Oem6 => TuiKey.SeekForward, // ]
        ConsoleKey.D0 or ConsoleKey.NumPad0 or ConsoleKey.Home => TuiKey.Restart,
        ConsoleKey.Q or ConsoleKey.Escape => TuiKey.Quit,
        _ => key.KeyChar switch
        {
            '+' or '=' => TuiKey.Faster,
            '-' or '_' => TuiKey.Slower,
            '[' => TuiKey.SeekBack,
            ']' => TuiKey.SeekForward,
            _ => null,
        },
    };
}
