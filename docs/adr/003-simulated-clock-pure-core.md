# ADR-003: Simulated clock and a pure core

## Status

Accepted (v1-easy, 2026-10-21)

## Context

We want to treat the system as an experiment: run scenarios, measure, change one thing, and measure again.
That needs runs that are reproducible bit for bit, fast (hours of building time in milliseconds), and testable
without `Thread.Sleep`. Real time and real threads make all three hard. The spec also asks for thread safety;
that matters once several elevators and concurrent requests exist (Medium level).

## Decision

- **Pure core.** `Elevator.Domain` and `Elevator.Policy` have no threads, locks, sleeps, I/O or `DateTime.Now`.
  The elevator advances only through `Tick(SimTime now)`. It returns events (`DomainEvent`) instead of logging
  them.
- **Two kinds of time.**
  - `SimTime` is a distinct type holding integer milliseconds of simulated time. Every metric in the report
    (wait, ride, sim time) is in sim seconds.
  - Wall-clock measurements (assignment latency, in v2) will use a separate type and are never mixed with
    `SimTime`.
  - Integers avoid floating-point drift, so logs are byte-identical.
- **Deadline-driven ticking.** Each timed phase (one floor of travel, doors opening, dwell, doors closing) has a
  deadline. `Tick(now)` handles every deadline ≤ `now` one at a time, at its exact instant.
- **Discrete-event runner in the shell.** `ScenarioRunner` steps a `SimClock` in fixed ticks (default 100 ms).
  Within a tick it jumps from event to event: elevator deadlines first, then passenger arrivals at the same instant,
  then passenger reactions (boarding and car calls) at that same instant. The tick only bounds a step, so **the event
  log is identical for any tick size**. A test checks this for ticks from 1 ms to 60 s. This is what will let the
  TUI run at 0.25×…50× without changing results.
- **One seeded RNG** (`new Random(scenario.Seed)`) per run, used only by traffic generation.
- **Concurrency deferred to v2.** v1's `ElevatorController` is synchronous and single-threaded. `Elevator.Runtime`
  exists as an empty project. In v2 it will host one actor per elevator (a loop plus a `Channel<T>` mailbox), a
  single dispatcher reading a bounded request channel, and a watchdog. These wrap the same pure core, so thread
  safety is achieved by single-writer ownership rather than locks around domain state.
- **Constant speed.** There is no acceleration model in v1: every floor takes `floorTravelMs`, and the decision to
  stop is taken when the elevator is level with a floor.

## Consequences

- The domain and policy tests run in milliseconds, with no sleeps and no flakiness.
- A run is fully described by (scenario, seed, policy). The JSONL log can be diffed between runs, and between
  strategies on the same seed.
- v1 does not yet meet the spec's thread-safety requirement. That is intentional, because one elevator driven by
  one loop has no shared state. Medium adds it without touching the domain.
- The simplified physics affects absolute numbers but not comparisons between strategies, which all run on the
  same model.

## Amendment: steppable engine (shared v3 contract, `a00d01f`)

The TUI needs to drive the simulation tick by tick, so the deterministic runner is now public as
`SimEngine.Create` → `AdvanceTo(t)` (any step sizes) → `Finish()`. `Run` is exactly that loop with fixed ticks.

- **The tick-size guarantee still holds, now at the API level.** A property test checks that random step sequences
  give a log byte-identical to `Run`'s.
- **Commands enter only in sim time.** `Post(to, message)` delivers at `Now`, after anything already due, so
  operator and fault commands stay deterministic and replayable.
- **Read-only views for rendering.** `Elevators` and `Waiting` are views that never drive the engine.

## Amendment: where the clocks live (ADR-019)

`IClock` and `RealClock` moved from `Elevator.Simulation` to `Elevator.Runtime`, because the runtime is what uses a
wall-driven clock, and the system may not depend on the lab. `SimClock` stays in Simulation and implements the
runtime's `IClock`. The pure core is unchanged: it still never sees a clock.
