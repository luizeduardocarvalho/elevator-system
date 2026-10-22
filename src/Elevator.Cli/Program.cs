using Elevator.Cli;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("elevator");
    config.UseStrictParsing(); // an unknown option is an error: a removed flag (ADR-018) must not run a different experiment silently
    config.AddCommand<RunCommand>("run")
        .WithDescription("Run a YAML scenario headless, write the JSONL event log and print a report.")
        .WithExample("run", "scenarios/v1-basic.yaml", "--verbose");
    config.AddCommand<BenchCommand>("bench")
        .WithDescription("Compare strategies across seeds (paired, with 95% CIs) and write a markdown results file.")
        .WithExample("bench", "--dispatch", "nearest,cost", "--seeds", "20");
    config.AddCommand<PerfCommand>("perf")
        .WithDescription("Real clock, real threads: assignment latency, lost/duplicated, memory soak; PASS/FAIL against the spec SLOs.")
        .WithExample("perf", "--levels", "100,1000,10000");
    config.AddCommand<TuiCommand>("tui")
        .WithDescription("Live terminal UI: building, metrics, ops and event tail. space pause · n step · +/- speed · ←/→ select · m/e/s faults · [[ ]] seek · q quit.")
        .WithExample("tui", "scenarios/lunch.yaml", "--seed", "3", "--dispatch", "nearest");
    config.AddCommand<HeatmapCommand>("heatmap")
        .WithDescription("The shape of a working day: wait by floor × time of day, as a coloured heatmap (from a scenario or a log).")
        .WithExample("heatmap", "day")
        .WithExample("heatmap", "day", "--dispatch", "nearest")
        .WithExample("heatmap", "day", "--vs", "nearest");
    config.AddCommand<ReplayCommand>("replay")
        .WithDescription("The same view driven from a JSONL event log; seeking re-folds the log.")
        .WithExample("replay", "out/lunch-seed1.jsonl", "--speed", "50");
});
return app.Run(args);
