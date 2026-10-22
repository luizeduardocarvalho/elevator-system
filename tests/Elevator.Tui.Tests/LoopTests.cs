using System.Security.Cryptography;
using System.Text;
using Elevator.Domain;
using Elevator.Domain.Events;
using Elevator.Simulation;
using Elevator.Tui.Loop;
using Elevator.Tui.State;
using Elevator.Tui.View;

namespace Elevator.Tui.Tests;

public class LoopTests
{
    private static string Hash(IEnumerable<DomainEvent> events) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Repo.ToJsonl(events))));

    private static async Task<LiveSession> RunLive(Scenario scenario, double? speed, params (int, TuiKey)[] keys)
    {
        var session = new LiveSession(scenario);
        var loop = new TuiLoop(session, new Pacer(speed), new FakeClock(), new ScriptedKeys(keys), new CapturingPresenter(), new LoopOptions(ExitAtEnd: true));
        await loop.RunAsync();
        Assert.True(session.AtEnd);
        return session;
    }

    [Theory]
    [InlineData("lunch.yaml")]
    [InlineData("demo.yaml")]
    [InlineData("chaos.yaml")]
    [InlineData("fleet.yaml")]
    public async Task Same_seed_gives_the_same_event_log_hash_at_half_speed_ten_times_and_headless(string name)
    {
        var scenario = Repo.Load(name);
        var half = await RunLive(scenario, 0.5);
        var ten = await RunLive(scenario, 10);

        var explained = SimEngine.Create(scenario, options: new SimEngineOptions(EmitDispatchEvaluations: true));
        while (!explained.Finished) explained.AdvanceTo(explained.Now.AddMs(ScenarioRunner.DefaultTickMs));
        var headless = explained.Finish().Events;

        Assert.Equal(Hash(headless), Hash(half.Log));
        Assert.Equal(Hash(headless), Hash(ten.Log));
        Assert.Equal(Hash(SimEngine.Run(scenario).Events), Hash(ten.Log.Where(e => e is not DispatchEvaluated)));
        Assert.IsType<RunEnded>(ten.Log[^1]);
    }

    [Fact]
    public async Task Speed_changes_and_pauses_mid_run_do_not_change_the_log()
    {
        var scenario = Repo.Load("lunch.yaml");
        var reference = await RunLive(scenario, 10);
        var fiddled = await RunLive(scenario, 0.25,
            (3, TuiKey.Faster), (10, TuiKey.Faster), (15, TuiKey.Pause), (20, TuiKey.Step), (21, TuiKey.Step), (25, TuiKey.Pause),
            (30, TuiKey.Faster), (40, TuiKey.Faster), (41, TuiKey.Faster), (42, TuiKey.Faster), (43, TuiKey.Faster));
        Assert.Equal(Hash(reference.Log), Hash(fiddled.Log));
    }

    [Fact]
    public async Task Pause_then_single_step_advances_exactly_one_tick()
    {
        var session = new LiveSession(Repo.Load("lunch.yaml"));
        var loop = new TuiLoop(session, new Pacer(10), new FakeClock(), new ScriptedKeys((0, TuiKey.Pause), (5, TuiKey.Step)), new CapturingPresenter(),
            new LoopOptions(MaxFrames: 10));
        await loop.RunAsync();
        Assert.Equal((1L, new Instant(Pacer.TickMs)), (loop.Ticks, session.Now));
    }

    [Fact]
    public async Task Ten_times_speed_advances_half_a_sim_second_per_frame()
    {
        var session = new LiveSession(Repo.Load("lunch.yaml"));
        var loop = new TuiLoop(session, new Pacer(10), new FakeClock(), new ScriptedKeys(), new CapturingPresenter(), new LoopOptions(MaxFrames: 21));
        await loop.RunAsync();
        Assert.Equal(new Instant(10_000), session.Now);
        Assert.Equal("10.0×", loop.BuildScreen().Status.AchievedSpeed);
    }

    private sealed class SlowSession(FakeClock clock) : ISession
    {
        public string Source => "LIVE";
        public BuildingState State { get; } = new();
        public Instant Now { get; private set; }
        public bool AtEnd => false;
        public bool ReadOnly => false;
        public bool InjectorNoOp => true;
        public void Step()
        {
            clock.Now += TimeSpan.FromMilliseconds(5);
            Now = Now.AddMs(Pacer.TickMs);
        }
        public void SeekTo(Instant target) => Now = target;
        public string Command(TuiKey key, int elevator) => "";
    }

    [Fact]
    public async Task Max_runs_ticks_until_the_frame_budget_is_spent()
    {
        var clock = new FakeClock();
        var loop = new TuiLoop(new SlowSession(clock), new Pacer(null), clock, new ScriptedKeys(), new CapturingPresenter(), new LoopOptions(MaxFrames: 3));
        await loop.RunAsync();
        Assert.Equal(3 * 7, loop.Ticks);
    }

    [Fact]
    public async Task A_slow_frame_drops_its_excess_and_the_achieved_speed_shows_it()
    {
        var clock = new FakeClock();
        var pacer = new Pacer(50);
        var loop = new TuiLoop(new SlowSession(clock), pacer, clock, new ScriptedKeys(), new CapturingPresenter(), new LoopOptions(MaxFrames: 40));
        await loop.RunAsync();
        Assert.Equal(0, pacer.AccumulatorMs);
        var achieved = double.Parse(loop.BuildScreen().Status.AchievedSpeed.TrimEnd('×'), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(achieved, 5, 20);
    }

    [Fact]
    public async Task Arrow_keys_select_elevators_and_wrap()
    {
        var loop = new TuiLoop(new LiveSession(Repo.Load("lunch.yaml")), new Pacer(10), new FakeClock(),
            new ScriptedKeys((1, TuiKey.SelectPrevious), (2, TuiKey.SelectNext), (2, TuiKey.SelectNext)), new CapturingPresenter(), new LoopOptions(MaxFrames: 4));
        await loop.RunAsync();
        Assert.Equal(1, loop.Selected);
    }

    [Fact]
    public async Task Fault_keys_reach_the_real_injector_and_take_effect_in_the_simulation()
    {
        var session = new LiveSession(Repo.Load("lunch.yaml"));
        var presenter = new CapturingPresenter();
        var loop = new TuiLoop(session, new Pacer(10), new FakeClock(),
            new ScriptedKeys((2, TuiKey.SelectNext), (3, TuiKey.Maintenance), (4, TuiKey.SelectNext), (5, TuiKey.Emergency),
                (6, TuiKey.SelectNext), (7, TuiKey.Stuck)), presenter, new LoopOptions(MaxFrames: 9));
        await loop.RunAsync();
        Assert.Equal([(1, Domain.Faults.FaultKind.MaintenanceOn), (2, Domain.Faults.FaultKind.EmergencyStop), (3, Domain.Faults.FaultKind.Stuck)],
            session.Commands.Select(c => (c.ElevatorId, c.Kind)));
        Assert.Equal(60_000, session.Commands[2].DurationMs);
        var status = presenter.Last!.Status;
        Assert.Equal("faults", status.Injector);
        Assert.DoesNotContain("no-op", status.Message, StringComparison.Ordinal);

        Assert.Equal([(1, Domain.Faults.FaultKind.MaintenanceOn), (2, Domain.Faults.FaultKind.EmergencyStop), (3, Domain.Faults.FaultKind.Stuck)],
            session.Log.OfType<Domain.Events.FaultCommandApplied>().Select(f => (f.ElevatorId, f.Kind)));
        Assert.Contains(session.Log, e => e is Domain.Events.MaintenanceRequested { ElevatorId: 1 });
        Assert.Contains(session.Log, e => e is Domain.Events.EmergencyStopped { ElevatorId: 2 });
    }

    [Fact]
    public async Task Quit_stops_the_loop()
    {
        var loop = new TuiLoop(new LiveSession(Repo.Load("lunch.yaml")), new Pacer(10), new FakeClock(), new ScriptedKeys((3, TuiKey.Quit)), new CapturingPresenter());
        var result = await loop.RunAsync();
        Assert.Equal((3, true), (result.Frames, result.Quit));
    }

    [Fact]
    public void Key_map_covers_the_documented_bindings()
    {
        static TuiKey? K(ConsoleKey key, char c = '\0') => KeyMap.From(new ConsoleKeyInfo(c, key, false, false, false));
        Assert.Equal(TuiKey.Pause, K(ConsoleKey.Spacebar, ' '));
        Assert.Equal(TuiKey.Step, K(ConsoleKey.N, 'n'));
        Assert.Equal(TuiKey.Faster, K(ConsoleKey.OemPlus, '+'));
        Assert.Equal(TuiKey.Slower, K(ConsoleKey.OemMinus, '-'));
        Assert.Equal(TuiKey.SelectPrevious, K(ConsoleKey.LeftArrow));
        Assert.Equal(TuiKey.SelectNext, K(ConsoleKey.RightArrow));
        Assert.Equal((TuiKey?)TuiKey.Maintenance, K(ConsoleKey.M, 'm'));
        Assert.Equal(TuiKey.Emergency, K(ConsoleKey.E, 'e'));
        Assert.Equal(TuiKey.Stuck, K(ConsoleKey.S, 's'));
        Assert.Equal(TuiKey.SeekBack, K(ConsoleKey.Oem4, '['));
        Assert.Equal(TuiKey.SeekForward, K(ConsoleKey.Oem6, ']'));
        Assert.Equal(TuiKey.Quit, K(ConsoleKey.Q, 'q'));
        Assert.Null(K(ConsoleKey.Z, 'z'));
    }
}
