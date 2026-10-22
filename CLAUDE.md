# Elevator System — Take-home

## Goal
Take-home for an IC interview. Implement the Easy → Medium → Hard elevator system from the spec,
treated as an experiment: build, simulate, measure, evolve. I will present the result and defend
every decision, so clarity and evidence matter more than feature count.

## Vocabulary
Use the spec's terms: `Elevator` (never "car" in code), hall call = pickup request on a floor
(floor + direction), car call = destination pressed inside an elevator. Spec enums
`ElevatorState` and `Direction` must be kept as given (extend only with an ADR).

## Architecture (non-negotiable)
- **Pure core, threaded shell.**
  - `Elevator.Domain`: state machine with an explicit transition table. No threads, locks, sleeps or DateTime.Now.
    Advances only through `Tick(simTime)`. Invariant: never moves unless doors are closed.
  - `Elevator.Policy`: scheduling and dispatch strategies as pure functions over immutable snapshots
    (LOOK; CostBased, with Nearest as the overload fallback and baseline). `main` holds only the final design:
    superseded strategies are removed once measured, and live at the stage tags (`v1-easy`, `v2-medium`,
    `v3-full-grid`; ADR-018).
  - `Elevator.Runtime`: single-writer actors — one loop + `Channel<T>` mailbox per elevator, a single
    dispatcher consuming a bounded request channel, a watchdog. No shared mutable state between elevators.
  - `Elevator.Simulation`: `IClock` (SimClock / RealClock), seeded traffic generator (Poisson, time-varying
    rate), YAML scenario loader, fault injector. One seeded RNG → fully deterministic runs.
  - `Elevator.Metrics`: consumers of the event log only (never call into the domain).
  - `Elevator.Cli`: commands `run | tui | replay | bench | perf`.
- **The event log (JSONL) is the backbone.** The domain emits events; metrics, TUI and replay consume them.
- **Two kinds of time:** sim seconds (wait time, maintenance time…) vs wall-clock ms (assignment latency).
  Never mix them.
- **TUI** (Spectre.Console) reads snapshots at about 20 fps and never drives the simulation. Speed is controlled with a
  fixed sim tick plus an accumulator (0.25×…50×, MAX, pause, single-step), with a cap on ticks per frame and the
  actual achieved speed displayed. The same seed must produce an identical event log at any speed.

## Stack
.NET (latest LTS), xUnit, FsCheck (property tests), Spectre.Console, YamlDotNet,
BenchmarkDotNet, HdrHistogram. Nullable enabled, warnings as errors.

## Working rules
- Before coding a stage: propose the plan (types, files, tests) and wait for my approval.
- Tests first for domain and policy. No `Thread.Sleep` in tests — use SimClock.
- Never hold a lock across a sleep or await. Prefer channels over locks.
- Small commits with clear messages. Tag each finished stage (`v1-easy`, `v2-medium`, `v3-hard`).
- Each architectural decision gets an ADR in `docs/adr/` (Nygard format: Context, Decision, Status,
  Consequences). Superseded ADRs stay, marked "Superseded by ADR-00X". Cite measured sim results as
  evidence when a decision replaces an earlier one.
- Explain non-obvious algorithms in comments and in your messages to me — I need to defend them.
- When unsure about a spec interpretation, ask; don't guess silently.
