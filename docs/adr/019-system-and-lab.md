# ADR-019: The lab depends on the system, never the reverse

## Status

Accepted (2026-10-22). Amends ADR-003 (where the clocks live) and ADR-007 (where the JSONL format lives).

## Context

The code has two kinds of project:

- **The system** is what would ship: the elevator state machine, the policies, the threaded runtime, the event log
  format, and the metrics that read it.
- **The lab** is what we use to study the system: the deterministic simulator, scenarios and traffic, perf and the
  throughput ramp, the TUI, and the CLI.

Until now `Elevator.Runtime`, the production system, referenced `Elevator.Simulation`, the lab. Runtime used these
lab types:

| Used by Runtime | Lived in Simulation as |
|---|---|
| Writing the JSONL log (perf) | `JsonlEventLog` |
| Its wall-driven clock | `RealClock`, `IClock` |
| Building elevators and the access pipeline | `Building.CreateElevator`, `Building.CreateAccess`, `AccessConfig` |
| Message delays | `MessageLatency` |
| Taking over a simulation mid-run (ADR-015) | `EngineHandover` |
| Its defaults | `SimEngine.OverloadThreshold`, `Scenario.DefaultWatchdogTimeoutMs` |
| Options from a scenario | `RuntimeOptions.From(Scenario)` |
| Feeding scripted arrivals | `ElevatorSystem.PlayPassengers(IEnumerable<Passenger>)` |
| Perf and the ramp | `PerfRunner`, `ThroughputRamp` (scenarios, `PeakMoment`) |

So shipping the runtime meant shipping the YAML loader, the traffic generator and the simulator. Worse, a change
made for an experiment could change production code without anyone noticing.

## Decision

**The system never references the lab. The lab may reference the system.**

```
System:  Domain ← Policy ← Runtime ─→ Metrics ─→ Domain
         Domain ← EventLog
Lab:     Simulation → {Domain, Policy, Runtime}
         Lab        → {Runtime, Simulation, Metrics, EventLog}
         Tui        → {Simulation, Metrics, EventLog}
         Cli        → everything
```

Each shared piece moved to where its concept lives, rather than into a grab-bag project:

| Piece | New home | Why there |
|---|---|---|
| `JsonlEventLog` | new `Elevator.EventLog` (refs Domain only) | The wire format belongs to the system: the runtime writes it. The event *records* stay in `Domain.Events`, because the domain emits them; moving them up would invert the dependency. |
| `Building.CreateElevator` | `ElevatorType.CreateElevator` (Domain) | An elevator built from its type. Same body. |
| `AccessConfig`, `Building.CreateAccess` | `AccessConfig.CreatePipeline()` (Policy.Access) | The config and the policies it composes sit together. |
| `MessageLatency`, `EngineHandover` → `Handover` | `Domain.Control` | They parameterise and carry handlers, next to `DispatcherOptions`. |
| `IClock`, `RealClock` | `Elevator.Runtime` | The runtime's clock. `SimClock` stays in Simulation and implements the runtime's `IClock`. |
| Defaults (overload 200, watchdog 30 s) | `RuntimeOptions.DefaultOverloadThreshold`, `DefaultWatchdogTimeoutMs` | The runtime owns its defaults. Scenario and SimEngine read them: one source per value. |
| `RuntimeOptions.From(Scenario)` | `Scenario.ToRuntimeOptions(speed)` in `Elevator.Lab` | Mapping a lab scenario onto the system is the lab's job. |
| `PlayPassengers(Passenger…)` | `PlayPassengers(Request…)` | `Request` is the domain's type; callers map `Passenger.ToRequest()`. Same order (timestamp, id). |
| `PerfRunner`, `ThroughputRamp`, `RampAnalysis` | new `Elevator.Lab` | Experiments run against the system, not part of it. Their tests moved to `Elevator.Lab.Tests`. |

**The order of the moves was forced.** `SimClock` (lab) implements `IClock`, so `IClock` can only move into Runtime
once Simulation is allowed to reference Runtime. That is only possible after Runtime's last reference to Simulation
is gone; otherwise the projects form a cycle. So the clocks moved last, in the same commit that flipped the reference.
Everything before that went to homes below both shells (Domain, Policy, EventLog), so every commit built and passed.

**An architecture test enforces the rule** (`tests/Elevator.Architecture.Tests`). For Domain, Policy, EventLog,
Runtime and Metrics it checks three things. Each catches a case the others miss:

1. *Declared:* the transitive `ProjectReference` closure contains no lab project. This is the only check that catches
   an unused reference, which the compiler drops from the assembly.
2. *Compiled:* the assembly's references name no lab assembly.
3. *Namespaces:* every type is under its own assembly's namespace, and no `TypeReference` points into
   `Elevator.Simulation`, `Elevator.Lab`, `Elevator.Tui` or `Elevator.Cli`. This is the only check that catches
   lab-named code placed in a system project.

A fourth test requires every `src/` project to be classified as system or lab, so a new project cannot skip the rule.
Each check was seen failing on a deliberate probe before the test was committed.

### Renames that follow from the split

Once the runtime stopped depending on the simulator, two names said "simulation" about things the system owns:

- **`SimTime` → `Instant`.** The runtime derives the same type from the wall clock (`RealClock`), so it is a point in
  a run's time, not only a simulated one. It is still a distinct integer-millisecond type that never mixes with
  wall-clock durations (ADR-008). The `t` field in the log is unchanged.
- **`SimulationStarted`/`SimulationEnded` → `RunStarted`/`RunEnded`, in code only.** The JSON discriminators keep the
  old strings (`[JsonDerivedType(typeof(RunStarted), "SimulationStarted")]`), so new logs are byte-identical and every
  old log replays without a reader change or a schema bump. A schema bump would have broken the byte-identity check.
  A test reads lines from a pre-rename log and writes them back identically.

Names that describe the lab (`SimClock`, `SimEngine`, `MaxSimTime`, the report's `SimTimeS`) and parameter names such
as `Tick(simTime)` are unchanged.

### Alternatives considered

- **A shared `Elevator.Shell` project for everything both shells need.** It would keep Simulation from seeing
  Runtime, but it would mix a clock, config records, a factory and defaults in one project with no single concept.
  Rejected in favour of natural homes.
- **`IClock` in Domain.** The domain is handed times explicitly and never sees a clock (ADR-003); an interface there
  would invite injecting one. Rejected.

## Consequences

- **No behaviour change, measured.** After every commit:
  - the full suite passed. Wall-clock tests (the perf gate, the ramp smoke test and the chaos cross-check) failed
    in some runs under heavy machine load. Interleaved runs of the pre-change commit failed the same way.
  - `run` on all 9 scenarios, a 3-seed `bench` and `heatmap day` were diffed against a baseline taken before the
    first commit. The output was byte-identical: 115,310 event-log lines plus every report.

  The only line filtered out was bench's wall-time line.
- **Simulation now references Runtime**, for `IClock` and the defaults. The lab's simulator can see the threaded
  runtime's types. It uses none of them except those two. This is the allowed direction.
- **`IClock` and `SimClock` have no consumers.** SimEngine tracks its own `Now`, and only `RealClock` is used. Both are
  kept as the documented abstraction (CLAUDE.md, ADR-003); deleting them is a separate decision.
- **`Runtime → Metrics` remains**, for the `WallDuration` type in `ActorStats` and `Assignment`. Both are system
  projects, so the rule holds. It is a value type, not a call into a consumer.
- **The cross-check tests** (`CrossCheckTests`, `ChaosCrossCheckTests`, `SteadyStateTests`) live in
  `Elevator.Runtime.Tests` and reference the lab, because they compare the runtime with the simulator. Test projects
  are outside the rule.
- **`ElevatorSystem.FromEngine(Handover, …)`** keeps its name: it still takes over from an engine. The record lost the
  "Engine" prefix because the domain does not know what produced it.
