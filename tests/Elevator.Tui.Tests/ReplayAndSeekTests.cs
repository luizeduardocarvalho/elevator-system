using System.Text.Json;
using Elevator.Domain;
using Elevator.Domain.Control;
using Elevator.Domain.Events;
using Elevator.Domain.Faults;
using Elevator.Simulation;
using Elevator.Tui.Loop;
using Elevator.Tui.State;
using Elevator.Tui.View;

namespace Elevator.Tui.Tests;

public class ReplayAndSeekTests
{
    /// <summary>What must match between two screens of the same sim instant: everything but who is driving.</summary>
    private static string Content(ScreenModel m) =>
        JsonSerializer.Serialize(new { m.Building, m.Metrics, m.Ops, m.Tail, m.Status.Clock, m.Status.Title, m.Status.Finished });

    private static ScreenModel Screen(ISession s) => ViewModel.Build(s.State, new ViewOptions(140, 45, Selected: 2));

    private static ReplaySession ReplayOf(IEnumerable<DomainEvent> log) => new(LogReader.Read(new StringReader(Repo.ToJsonl(log))));

    /// <summary>Records (sim time, screen) every <paramref name="every"/> frames.</summary>
    private sealed class SamplingPresenter(Func<ISession> session, int every) : IPresenter
    {
        private int _frame;

        public (int Width, int Height) Size => (140, 45);

        public List<(SimTime At, ScreenModel Model)> Samples { get; } = [];

        public void Present(ScreenModel model)
        {
            if (_frame++ % every == 0) Samples.Add((session().Now, model));
        }
    }

    private static async Task<(LiveSession Session, List<(SimTime At, ScreenModel Model)> Samples)> RecordLive(string scenario, double speed)
    {
        var session = new LiveSession(Repo.Load(scenario));
        var presenter = new SamplingPresenter(() => session, every: 37);
        await new TuiLoop(session, new Pacer(speed), new FakeClock(), new ScriptedKeys(), presenter, new LoopOptions(ExitAtEnd: true)).RunAsync();
        return (session, presenter.Samples);
    }

    /// <summary>
    /// Record a live run driven by the TUI loop, write its log as JSONL, replay it: at every sampled sim time the
    /// replay's screen equals the live screen (building, metrics, ops, tail, clock).
    /// </summary>
    [Theory]
    [InlineData("lunch.yaml")]
    [InlineData("demo.yaml")]
    public async Task Replay_of_a_recorded_tui_log_shows_the_same_screens_as_the_live_run(string scenario)
    {
        var (live, samples) = await RecordLive(scenario, 10);
        Assert.True(samples.Count > 20);
        var replay = ReplayOf(live.Log);
        foreach (var (at, model) in samples)
        {
            replay.SeekTo(at);
            Assert.Equal(Content(model), Content(ViewModel.Build(replay.State, new ViewOptions(140, 45))));
        }
        replay.SeekTo(live.Now);
        Assert.Equal(Content(Screen(live)), Content(Screen(replay)));
        Assert.True(replay.AtEnd && replay.State.Ended);
    }

    [Fact]
    public async Task Replay_driven_by_the_loop_matches_live_frame_by_frame()
    {
        var (live, liveSamples) = await RecordLive("lunch.yaml", 10);
        var replay = ReplayOf(live.Log);
        var presenter = new SamplingPresenter(() => replay, every: 37);
        await new TuiLoop(replay, new Pacer(10), new FakeClock(), new ScriptedKeys(), presenter, new LoopOptions(ExitAtEnd: true)).RunAsync();
        Assert.Equal(liveSamples.Count, presenter.Samples.Count);
        Assert.Equal(liveSamples.Select(s => (s.At, Content(s.Model))), presenter.Samples.Select(s => (s.At, Content(s.Model))));
    }

    [Fact]
    public void Replay_of_a_headless_run_log_without_explanations_works()
    {
        var scenario = Repo.Load("lunch.yaml");
        var result = SimEngine.Run(scenario);
        var replay = ReplayOf(result.Events);
        replay.SeekTo(new SimTime(long.MaxValue / 2));
        Assert.True(replay.AtEnd);
        Assert.Equal((result.Delivered, true), (replay.State.Delivered, replay.State.Ended));
        Assert.Equal($"{result.Delivered} / {result.Delivered}", Screen(replay).Metrics.Served);
    }

    [Fact]
    public void Replay_survives_unknown_and_garbage_lines()
    {
        var jsonl = Repo.ToJsonl(SimEngine.Run(Repo.Load("v1-basic.yaml")).Events).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        jsonl.Insert(5, """{"seq":99,"type":"ElevatorTeleported","t":3000,"elevatorId":0,"to":7}""");
        jsonl.Insert(9, "}{ not json");
        var replay = new ReplaySession(LogReader.Read(new StringReader(string.Join('\n', jsonl))));
        replay.SeekTo(new SimTime(100_000));
        var tail = ViewModel.Build(replay.State, new ViewOptions(Height: 400)).Tail;
        Assert.Contains(tail, l => l is { Kind: "ElevatorTeleported", Text: "elevatorId=0 to=7" });
        Assert.Contains(tail, l => l.Kind == "UNREADABLE");
        Assert.Equal(3, replay.State.Delivered);
    }

    [Fact]
    public void Replay_seek_back_refolds_from_the_start()
    {
        var log = SimEngine.Run(Repo.Load("lunch.yaml")).Events;
        var seeked = ReplayOf(log);
        seeked.SeekTo(new SimTime(900_000));
        seeked.SeekTo(new SimTime(450_000));
        var direct = ReplayOf(log);
        direct.SeekTo(new SimTime(450_000));
        Assert.Equal(Content(Screen(direct)), Content(Screen(seeked)));
    }

    private static void StepTo(LiveSession s, long ms)
    {
        while (s.Now.Ms < ms && !s.AtEnd) s.Step();
    }

    [Fact]
    public void Live_seek_back_equals_a_fresh_run_to_the_same_time_and_continues_identically()
    {
        var scenario = Repo.Load("lunch.yaml");
        var seeked = new LiveSession(scenario);
        StepTo(seeked, 900_000);
        seeked.SeekTo(new SimTime(450_000));
        var fresh = new LiveSession(scenario);
        StepTo(fresh, 450_000);

        Assert.Equal(Repo.ToJsonl(fresh.Log), Repo.ToJsonl(seeked.Log));
        Assert.Equal(Content(Screen(fresh)), Content(Screen(seeked)));

        StepTo(seeked, long.MaxValue);
        StepTo(fresh, long.MaxValue);
        Assert.Equal(Repo.ToJsonl(fresh.Log), Repo.ToJsonl(seeked.Log));
    }

    /// <summary>
    /// A stand-in for the Hard stage's injector with a visible effect: each command posts a new passenger, so a
    /// command re-applied at the wrong time (or not at all) changes the log.
    /// </summary>
    private sealed class PostingInjector(SimEngine engine) : IFaultInjector
    {
        private int _count;

        public bool IsNoOp => false;

        public void Inject(FaultCommand command) => engine.Post(Address.Passengers,
            new PassengerArrives(new Request(2, 9, Direction.Up, engine.Now, 10_000 + _count++)));
    }

    [Fact]
    public void Live_seek_back_re_applies_operator_commands_at_their_sim_time_and_drops_later_ones()
    {
        var scenario = Repo.Load("lunch.yaml");
        var seeked = new LiveSession(scenario, faultInjector: e => new PostingInjector(e));
        StepTo(seeked, 300_000);
        seeked.Command(TuiKey.Stuck, 1);
        StepTo(seeked, 600_000);
        seeked.Command(TuiKey.Maintenance, 2);
        StepTo(seeked, 900_000);
        Assert.Equal(2, seeked.Log.OfType<PassengerArrived>().Count(p => p.PassengerId >= 10_000));

        seeked.SeekTo(new SimTime(450_000));
        Assert.Equal([new FaultCommand(new SimTime(300_000), 1, FaultKind.Stuck, 60_000)], seeked.Commands);

        var fresh = new LiveSession(scenario, faultInjector: e => new PostingInjector(e));
        StepTo(fresh, 300_000);
        fresh.Command(TuiKey.Stuck, 1);
        StepTo(fresh, 450_000);
        Assert.Equal(Repo.ToJsonl(fresh.Log), Repo.ToJsonl(seeked.Log));
        Assert.Contains(seeked.Log, e => e is PassengerArrived { PassengerId: 10_000, At.Ms: 300_000 });
        Assert.False(seeked.InjectorNoOp);

        StepTo(seeked, 700_000);
        StepTo(fresh, 700_000);
        Assert.Equal(Repo.ToJsonl(fresh.Log), Repo.ToJsonl(seeked.Log)); // the dropped 600 s command never comes back
    }

    [Fact]
    public async Task Seek_keys_in_the_loop_jump_back_and_forward_on_the_tick_grid()
    {
        var session = new LiveSession(Repo.Load("lunch.yaml"));
        var loop = new TuiLoop(session, new Pacer(50), new FakeClock(),
            new ScriptedKeys((100, TuiKey.Pause), (101, TuiKey.SeekBack), (102, TuiKey.SeekForward), (103, TuiKey.SeekForward), (104, TuiKey.SeekBack)),
            new CapturingPresenter(), new LoopOptions(MaxFrames: 106));
        await loop.RunAsync();
        // 99 frames × 50 ms × 50 = 247.5 s, tick-aligned; then −60 +60 +60 −60.
        Assert.Equal(new SimTime(247_500), session.Now);
        var fresh = new LiveSession(Repo.Load("lunch.yaml"));
        StepTo(fresh, 247_500);
        Assert.Equal(Repo.ToJsonl(fresh.Log), Repo.ToJsonl(session.Log));
        Assert.StartsWith("seek → 0:04:07.5", loop.BuildScreen().Status.Message, StringComparison.Ordinal);
    }
}
