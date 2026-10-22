# Elevator system

An elevator system in .NET 10, built in three stages from the [interview spec](docs/spec.md): one elevator (Easy),
four elevators with dispatch (Medium), and elevator types, access rules, and faults (Hard). Each stage is an
experiment. The code runs a deterministic simulation, the results are measured, and every design change cites the
measurement that justified it.

## Run it

You need the .NET 10 SDK.

1. Build the solution:

	```sh
	dotnet build
	```

2. Run a scenario headless:

	```sh
	dotnet run --project src/Elevator.Cli -- run scenarios/demo.yaml --verbose
	```

	The command prints every event, then a report table with wait and ride times. It writes the event log to
	`out/demo-seed2026.jsonl`.

3. Watch a scenario in the terminal UI:

	```sh
	dotnet run --project src/Elevator.Cli -- tui scenarios/lunch.yaml
	```

	Press `space` to pause, `n` to step, `+` and `-` to change speed, and `q` to quit. `--help` lists the other keys.

4. Run the tests:

	```sh
	dotnet test --filter "Category!=Perf"
	```

	The filter skips `PerfGateTests`, which run real threads under load and include a 20-second soak. Drop the filter to
	include them.

## The CLI

Every command takes `--help`. The scenarios live in `scenarios/`.

| Command | What it does |
|---|---|
| `run <scenario>` | Runs a YAML scenario headless, writes the JSONL event log, and prints a report. |
| `bench` | Compares strategies across seeds, paired on the same passengers, with 95% confidence intervals. Writes a markdown results file. |
| `perf` | Runs the threaded runtime on the real clock. Measures assignment latency, lost and duplicated requests, and memory, then prints PASS or FAIL against the spec targets. |
| `tui <scenario>` | Shows the building, metrics, and event tail live. Speed runs from 0.25× to 50× or max, with pause and single-step. |
| `replay <log>` | Shows the same view, driven from a JSONL event log. |
| `heatmap <scenario or log>` | Shows wait time by floor and time of day. `--vs` draws the difference between two strategies. |

Scheduling (`--scheduling`) is `fifo` or `look`. Dispatch (`--dispatch`) is `roundrobin`, `nearest`, or `cost`.
Every strategy stays selectable, so any earlier result can be rerun.

## How the code is laid out

The core is pure and the shell is threaded. The domain and the policies are functions of their inputs: no
threads, no locks, no clock. The same seed gives a byte-identical event log at any tick size and any UI speed.

| Project | Role |
|---|---|
| `src/Elevator.Domain` | The elevator state machine with an explicit transition table. It advances only through `Tick(simTime)` and never moves with its doors open. |
| `src/Elevator.Policy` | Scheduling (FIFO, LOOK) and dispatch (round robin, nearest, cost-based) as pure functions over snapshots. |
| `src/Elevator.Simulation` | The simulated clock, the seeded traffic generator, the YAML scenario loader, and the fault schedule. |
| `src/Elevator.Runtime` | The threaded system: one actor and mailbox per elevator, one dispatcher, and a watchdog. |
| `src/Elevator.Metrics` | Reports built only from the event log. |
| `src/Elevator.Tui` | The terminal UI. It reads snapshots and never drives the simulation. |
| `src/Elevator.Cli` | The `elevator` command. |
| `benchmarks/Elevator.Benchmarks` | BenchmarkDotNet measurements of one dispatch decision. |

The event log is the one record of a run. Metrics, the UI, and replay all read it.
[Architecture evolution](docs/architecture-evolution.md) shows how this layout grew stage by stage, with a diagram per
stage.

## What the measurements show

Each cell is the average and p95 wait in simulated seconds, over 20 held-out seeds. Both rows ran on the same
passengers: 20 floors, 4 elevators, capacity 10. The source is [`m5-next-trip`](docs/results/m5-next-trip.md).

| Strategy | Up-peak | Down-peak | Lunch |
|---|---|---|---|
| FIFO + nearest (baseline) | 26.9 / 103.9 | 251.1 / 584.6 | 95.4 / 325.7 |
| LOOK + cost-based dispatch (default) | 15.8 / 37.9 | 20.6 / 53.2 | 13.8 / 38.4 |

Under load, the threaded runtime assigns a request in 12.2 ms at p99 with 100 simultaneous requests and 53.6 ms with
1,000. The spec asks for under 100 ms. [Results](docs/results.md) has the full table, the method, and the exact
command behind each number. Simulation results regenerate byte-identically.

## Why it is built this way

Each decision has an architecture decision record in [`docs/adr/`](docs/adr/README.md). A record that a later one
replaced stays in place, marked as superseded, with the evidence for the change. Start with these:

- [ADR-003](docs/adr/003-simulated-clock-pure-core.md): why the core runs on a simulated clock.
- [ADR-004](docs/adr/004-look-scheduling.md): why LOOK replaced FIFO.
- [ADR-005](docs/adr/005-actors-vs-locks.md): why the runtime uses actors instead of locks.
- [ADR-006](docs/adr/006-dispatch-cost-function.md): how cost-based dispatch picks an elevator.
- [ADR-007](docs/adr/007-event-log-backbone.md): why the event log is the backbone.
- [ADR-015](docs/adr/015-ordering-rules.md): the ordering rules. FIFO keeps correctness, a priority lane handles urgency, and cost handles fairness.

The tags `v1-easy`, `v2-medium`, and `v3-hard` mark the end of each stage.
