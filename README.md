# Elevator system

An elevator system in .NET 10, built in three stages from the [interview spec](docs/spec.md): one elevator (Easy),
four elevators with dispatch (Medium), and elevator types, access rules, and faults (Hard). Each stage is an
experiment. The code runs a deterministic simulation, the results are measured, and every design change cites the
measurement that justified it.

## The stages and their tags

`main` holds only the final design. Each earlier stage is a git tag, so the Easy and Medium solutions can be read
and run exactly as they were submitted:

| Tag | Stage | What it holds |
|---|---|---|
| `v1-easy` | Easy | One elevator, floors 1–10, FIFO scheduling, the spec's `ElevatorController` (`requestElevator`, `processRequests`) |
| `v2-medium` | Medium | 3–5 elevators, floors 1–20, LOOK, cost-based dispatch, the threaded actor runtime. FIFO, round robin, nearest, rebalancing and both capacity modes are all selectable |
| `v3-hard` | Hard | Express, local and freight elevators, floor restrictions and VIP access, maintenance, emergency stop, watchdog, fault injection, the terminal UI |
| `v3-full-grid` | Hard, all strategies | The last commit where every strategy is still selectable. Every file in `docs/results/` reproduces here, byte for byte |
| `main` | Final | LOOK, cost-based dispatch (nearest as fallback), next-trip capacity. Superseded strategies removed ([ADR-018](docs/adr/018-prune-superseded-strategies.md)) |

To see a stage, check out its tag, for example `git checkout v1-easy`.

Where the spec's Easy and Medium items went in the final design:

| Spec item | Final design | Earlier form |
|---|---|---|
| FIFO scheduling | LOOK, which cut the down-peak wait from 255.6 s to 28.2 s ([ADR-004](docs/adr/004-look-scheduling.md)) | `v1-easy`, `v2-medium` |
| `ElevatorController` | `ElevatorSystem`, the threaded multi-elevator controller ([ADR-005](docs/adr/005-actors-vs-locks.md)) | `v1-easy` |
| `balanceLoad()` | The cost function's predicted-load term, applied on every assignment ([ADR-006](docs/adr/006-dispatch-cost-function.md)) | Periodic rebalancing at `v2-medium`, measured and dropped |

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
| `bench` | Compares dispatch policies across seeds, paired on the same passengers, with 95% confidence intervals. Writes a markdown results file. |
| `perf` | Runs the threaded runtime on the real clock. Measures assignment latency, lost and duplicated requests, and memory, then prints PASS or FAIL against the spec targets. |
| `tui <scenario>` | Shows the building, metrics, and event tail live. Speed runs from 0.25× to 50× or max, with pause and single-step. |
| `replay <log>` | Shows the same view, driven from a JSONL event log. |
| `heatmap <scenario or log>` | Shows wait time by floor and time of day. `--vs` draws the difference against another dispatch policy. |

Scheduling is LOOK. Dispatch (`--dispatch`) is `cost` (the default) or `nearest`. An unknown option is an error, so a
command written for an earlier stage fails instead of silently running something else. To rerun an earlier
comparison, check out `v3-full-grid`.

## How the code is laid out

The core is pure and the shell is threaded. The domain and the policies are functions of their inputs: no
threads, no locks, no clock. The same seed gives a byte-identical event log at any tick size and any UI speed.

| Project | Role |
|---|---|
| `src/Elevator.Domain` | The elevator state machine with an explicit transition table. It advances only through `Tick(simTime)` and never moves with its doors open. |
| `src/Elevator.Policy` | LOOK scheduling and dispatch (cost-based, nearest) as pure functions over snapshots. |
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
passengers: 20 floors, 4 elevators, capacity 10. The source is [`m5-next-trip`](docs/results/m5-next-trip.md). The
baseline no longer exists on `main`; its row reproduces at `v3-full-grid`.

| Strategy | Up-peak | Down-peak | Lunch |
|---|---|---|---|
| FIFO + nearest (baseline, `v3-full-grid`) | 26.9 / 103.9 | 251.1 / 584.6 | 95.4 / 325.7 |
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
- [ADR-017](docs/adr/017-request-validation-and-fail-stop.md): why an invalid request is refused at the boundary, and why a handler bug stops the whole system.
- [ADR-018](docs/adr/018-prune-superseded-strategies.md): why `main` holds only the final design, and where the rest lives.
