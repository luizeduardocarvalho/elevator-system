using Elevator.Cli;
using Spectre.Console.Cli;

// v1 registers `run`. `tui`, `replay`, `bench` and `perf` arrive in later stages.
var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("elevator");
    config.AddCommand<RunCommand>("run")
        .WithDescription("Run a YAML scenario headless, write the JSONL event log and print a report.")
        .WithExample("run", "scenarios/v1-basic.yaml", "--verbose");
});
return app.Run(args);
