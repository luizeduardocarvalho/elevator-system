# ADR-018: Remove superseded strategies; `main` is the final design

## Status

Accepted (2026-10-22). Supersedes the "stays selectable" clauses of ADR-001, ADR-006, ADR-009 and ADR-010, and the
working rule "all strategies stay selectable forever".

## Context

The system grew in three stages, and each stage replaced something with a measured improvement. Until now the
replaced code stayed in, selectable by flag, so every comparison could be rerun on `main`:

| Removed | Replaced by | Evidence (held-out seeds 21–40 unless noted) |
|---|---|---|
| FIFO scheduling (ADR-001) | LOOK (ADR-004) | [m2-look](../results/m2-look.md): wait avg/p95 at down-peak 255.6/592.5 s → 28.2/84.5 s (seeds 1–20); FIFO saturates |
| Round-robin dispatch | Cost-based (ADR-006), with nearest as fallback | [m1-baseline](../results/m1-baseline.md): round robin was the lower bound, 24.4 ± 2.2 s worse than FIFO + nearest at up-peak (seeds 1–20) |
| Hysteresis rebalancing (ADR-006, optional) | Sticky assignments | [m4-rebalance](../results/m4-rebalance.md): −0.8 ± 0.5 s at down-peak, not significant at up-peak or lunch; it moves about one passenger in eight to a different door |
| Capacity mode `defer` (ADR-009) | `next-trip` (ADR-010) | [m5-capacity-modes](../results/m5-capacity-modes.md): LOOK + Cost up-peak 16.4 → 15.8 s (−0.6 ± 0.4 s), no worse elsewhere |
| `ElevatorController` (Easy-stage API) | `ElevatorSystem` (ADR-005) | No production path called it. Its graceful rejection now lives at the runtime boundary (ADR-017) |

The cost was real. The superseded paths made up a large share of the dispatcher, the elevator and the projection:
Defer branches in four places, a rebalancing timer and message pair, and a scheduling dimension in every property
test. They also made `main` harder to read and defend: "which of these is the system?" had to be answered from
flags and defaults.

The evidence does not need the code to stay valid. Every results file records the exact command that produced
it, and the git history keeps the code that ran it.

## Decision

`main` holds only the final design:

- **Scheduling:** LOOK, fixed. `ISchedulingPolicy` stays as the seam between mechanics and policy (ADR-002), with
  one implementation.
- **Dispatch:** `cost` (default, ADR-006) or `nearest`. Nearest stays because it is the overload fallback, and the
  runtime's 1,000-request latency result depends on it. It is also the baseline that cost is compared against
  ([m3-cost-vs-nearest](../results/m3-cost-vs-nearest.md)).
- **Capacity:** next-trip, fixed.
- **Load balancing:** the cost function's predicted-load term, on every decision. The spec's `balanceLoad()`
  (`ElevatorSystem.BalanceLoad`) is kept and does nothing on purpose; its doc comment says why.

The superseded code lives at tags:

| Tag | Holds |
|---|---|
| `v1-easy` | The Easy stage: `ElevatorController`, FIFO, one elevator |
| `v2-medium` | The Medium stage: every strategy above, selectable |
| `v3-full-grid` | The last commit with every strategy selectable, after the ADR-015 regeneration. Every results file reproduces there, byte for byte |

**What does not change:**

- **The event log.** `SimulationStarted` still records `scheduling` ("look") and `capacityMode` ("next-trip"), so
  old logs replay and the contract (contract-v3) stands. The golden logs of `lunch.yaml` and `chaos.yaml` are
  byte-identical before and after this change. That is the check that only unused paths were removed.
- **The results files.** They stay as the record of the experiment. Their commands name strategies that `main` no
  longer has; the README and `docs/results.md` say to run them at `v3-full-grid`.

**The CLI now parses strictly.** `--scheduling`, `--rebalance` and `--capacity-mode` are gone. Spectre.Console
used to ignore unknown options, so `elevator run lunch.yaml --scheduling fifo` would have run LOOK without a word:
a silently different experiment. An unknown option is now an error.

## Consequences

- The final system is the only system on `main`. Defending it means defending one configuration, with the tags and
  results files as the history.
- The Easy and Medium spec items that the final design replaced (FIFO, `ElevatorController`, a separate
  `balanceLoad` pass) are shown at their tags. The README points there first, so a grader looking on `main` alone
  finds the pointer instead of a gap.
- `v1-basic.yaml` now runs under LOOK. Its hand trace was redone (`DeterministicScenarioTests`,
  `WaitAttributionTests`): waits 9/48/16 s against FIFO's 9/38/63 s, run ends at 62 s instead of 76 s.
- Property tests cover only the final configurations (cost or nearest; next-trip). They no longer exercise
  combinations nobody ships.
- `bench` compares dispatch policies only (`--dispatch`, `--strategies`). Rerunning M1–M5 requires checking out
  `v3-full-grid`.
- Removed tests: FIFO decisions, the round-robin rotation, rebalancing moves, Defer hand-backs, and the
  FIFO-vs-bitmask equivalence. The remaining suite passes, perf gate included.
