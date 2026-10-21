using Elevator.Cli;
using Spectre.Console.Cli;

// `tui` and `replay` arrive in a later stage.
var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("elevator");
    config.AddCommand<RunCommand>("run")
        .WithDescription("Run a YAML scenario headless, write the JSONL event log and print a report.")
        .WithExample("run", "scenarios/v1-basic.yaml", "--verbose");
    config.AddCommand<BenchCommand>("bench")
        .WithDescription("Compare strategies across seeds (paired, with 95% CIs) and write a markdown results file.")
        .WithExample("bench", "--scheduling", "fifo", "--dispatch", "roundrobin,nearest", "--seeds", "20");
    config.AddCommand<PerfCommand>("perf")
        .WithDescription("Real clock, real threads: assignment latency, lost/duplicated, memory soak; PASS/FAIL against the spec SLOs.")
        .WithExample("perf", "--levels", "100,1000,10000");
});
return app.Run(args);
