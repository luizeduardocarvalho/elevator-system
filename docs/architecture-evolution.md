# Architecture evolution

How the design grows stage by stage. Each version links to the ADRs that justify it.

## v1: Easy (tag `v1-easy`)

One elevator, floors 1–10, FIFO hall-call scheduling with real mechanics, a headless deterministic simulation.
See ADR-001 (FIFO), ADR-002 (state machine) and ADR-003 (sim clock and pure core).

```mermaid
flowchart LR
    subgraph Cli["Elevator.Cli"]
        RUN["run command"]
    end

    subgraph Simulation["Elevator.Simulation (shell)"]
        LOADER["ScenarioLoader<br/>YAML + validation"]
        TRAFFIC["PoissonTraffic<br/>seeded RNG"]
        RUNNER["ScenarioRunner<br/>SimClock ticks +<br/>discrete-event loop"]
        PAX["PassengerModel<br/>waiting / riding"]
        JSONL["JsonlEventLog"]
    end

    subgraph Domain["Elevator.Domain (pure)"]
        CTRL["ElevatorController<br/>requestElevator / selectFloor /<br/>processRequests"]
        ELEV["Elevator<br/>Tick(simTime)"]
        TABLE["TransitionTable"]
    end

    subgraph Policy["Elevator.Policy (pure)"]
        FIFO["FifoScheduling<br/>NextGoal(snapshot)"]
    end

    subgraph Metrics["Elevator.Metrics"]
        REPORT["ReportBuilder<br/>wait / ride avg·p95·max"]
    end

    RUN --> LOADER --> RUNNER
    TRAFFIC --> RUNNER
    RUNNER -- "hall calls, car calls, time" --> CTRL --> ELEV
    ELEV -- "every state change" --> TABLE
    ELEV -- "snapshot" --> FIFO -- "goal floor" --> ELEV
    ELEV -- "DomainEvents" --> RUNNER
    RUNNER <--> PAX
    RUNNER -- "events" --> JSONL -- "out/*.jsonl" --> REPORT --> RUN
```

Key properties:
- Arrows into Domain and Policy carry data only. Those projects never call out, read a clock or do I/O.
- The event log is the only input to Metrics. The `run` command builds its report by reading back the file it wrote.
- The same scenario and seed give a byte-identical log at any tick size.

Baseline measured at v1 (`scenarios/v1-poisson.yaml`, seed 42, FIFO): wait avg 77.0 s, p95 193.0 s,
max 242.0 s; 69/69 served.

## v2: Medium (tag `v2-medium`)

Four elevators on 20 floors with destination dispatch, LOOK, cost-based dispatch, capacity, and a real threaded
runtime next to the deterministic simulator. The ADRs:

| ADR | Decision |
|---|---|
| 004 | LOOK per elevator (supersedes 001) |
| 005 | actors and one shared protocol, not locks |
| 006 | the cost function, its fast projection, staleness, overlay and overload |
| 007 | the event log as backbone |
| 008 | metric definitions and two kinds of time |
| 009 | destination dispatch and capacity |
| 010 | next-trip under full load |

```mermaid
flowchart TB
    subgraph Pure["Pure core: no threads, clocks or I/O"]
        direction LR
        subgraph Domain["Elevator.Domain"]
            ELEV["Elevator<br/>state machine + mechanics<br/>(TransitionTable)"]
            subgraph Control["Control: one message protocol"]
                EH["ElevatorHandler<br/>accept / reject"]
                DH["DispatcherHandler<br/>assign · retry · overlay ·<br/>overload fallback · rebalance"]
                PH["PassengersHandler<br/>board · alight · left behind"]
            end
        end
        subgraph Policy["Elevator.Policy"]
            SCHED["FIFO · LOOK<br/>(list + bitmask Decide)"]
            DISP["RoundRobin · Nearest · Cost"]
            PROJ["StopProjection<br/>(RouteProjection = oracle)"]
        end
        EH --> ELEV
        ELEV --> SCHED
        DH --> DISP --> PROJ --> SCHED
    end

    subgraph Shells["Shells: deliver messages, keep time, no decisions"]
        direction LR
        SIM["SimEngine<br/>priority queue (time, seq)<br/>deterministic"]
        RT["Elevator.Runtime<br/>actor per elevator, dispatcher on<br/>bounded channel, RealClock"]
    end

    Shells -- "same messages" --> Control
    Control -- "DomainEvents" --> LOG["Event log (JSONL)"]
    LOG --> MET["Elevator.Metrics<br/>report · lifecycle · wait buckets · Student-t CIs"]
    CLI["Elevator.Cli<br/>run · bench · perf"] --> SIM
    CLI --> RT
    MET --> CLI
```

Key properties:
- **All decisions are in pure handlers and policies.** The deterministic engine and the actor runtime deliver the same
  messages to them; a cross-check test runs the same scenario through both.
- **The dispatcher never shares memory with elevators.** It sees snapshots (possibly stale) plus its own pending
  proposals (overlay); elevators have the final say (accept / reject).
- **Every measured claim comes from the event log, with paired CIs on common random numbers.**

**Results at v2** (wait avg / p95 in sim seconds, held-out seeds 21–40, next-trip):

| Profile | FIFO + Nearest (baseline) | LOOK + Nearest | LOOK + Cost (default) |
|---|---|---|---|
| up-peak | 26.9 / 103.9 | 20.7 / 59.4 | **15.8 / 37.9** |
| down-peak | 251.1 / 584.6 (saturated) | 29.3 / 87.7 | **20.6 / 53.2** |
| lunch | 95.4 / 325.7 | 21.0 / 78.1 | **13.8 / 38.4** |

Sources: [m5-next-trip](results/m5-next-trip.md), [m5-capacity-modes](results/m5-capacity-modes.md);
the progression is in [docs/results.md](results.md).

## v3: Hard

To be written at merge: elevator types, maintenance and emergency stops (with watchdog and fault injection), floor
restrictions and VIP, and the TUI.

**Hard-stage note (branch `stage4-hard`, ADR-012, ADR-013).** The v2 shape held. Types, eligibility and faults went
into the pure core and the shared handlers, so both shells got them at once:
- **Types are configuration** (`building.types`/`fleet`). The elevator and `StopProjection` replay each elevator's own
  timing and served floors, so cost dispatch predicts an express as an express.
- **Eligibility comes before cost.** A composable `IAccessPolicy` pipeline (served floors, capacity units, floor
  restrictions, VIP) runs in the dispatcher once per request. A denied elevator is invisible to every dispatch path; a
  request no elevator may take is refused, never lost.
- **Out of service is a mode, not a state** (`ElevatorMode`). Two new transition-table edges: `EmergencyHalt` and
  `EmergencyClose`.
- **Commands are messages** (`ApplyFault`) to the elevator's single owner. Emergency stops preempt: a priority control
  channel per actor, a priority key in the sim queue.
- **The watchdog rides on the dispatcher's snapshot stream,** plus a per-floor progress report.
- **Ops metrics come from the log only.**

With faults off and the default config, every v2 result regenerates byte-identically.
Results: [h1-chaos](results/h1-chaos.md), [h2-fleet](results/h2-fleet.md), [h3](results/h3-watchdog-timeout.md),
[h4](results/h4-watchdog-false-positives.md).
