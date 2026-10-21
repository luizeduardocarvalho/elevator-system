# ADR-004: LOOK scheduling per elevator

## Status

Accepted (v2-medium, 2026-10-21). Supersedes [ADR-001](001-fifo-scheduling.md) as the default scheduling policy.

## Context

Stage 2 moves to 4 elevators on 20 floors. Before changing any behaviour we measured the v1 FIFO policy with
destination dispatch and a "nearest elevator" dispatcher. That is the baseline, `fifo+nearest`, in
[`docs/results/m1-baseline.md`](../results/m1-baseline.md). The bench used 20 seeds per profile, peak 12
passengers/min, and common random numbers.

| Profile | Wait avg | Wait p95 | Journey avg |
|---|---|---|---|
| up-peak | 25.9 ± 2.0 s | 89.3 ± 8.6 s | 60.2 ± 2.1 s |
| down-peak | 255.6 ± 9.0 s | 592.5 ± 30.7 s | 280.3 ± 9.0 s |
| lunch | 99.1 ± 8.3 s | 353.5 ± 34.2 s | 124.7 ± 8.3 s |

FIFO saturates at down-peak. The wait attribution shows why: 95 s of the average wait is `travel_away` and 107 s
is `travel_toward`. Each trip serves the oldest call and drives past everyone else, so an elevator carries 1–2
people per trip. Up-peak hides this because lobby passengers share a single stop.

## Decision

- **LOOK is the default per-elevator scheduling policy** (`LookScheduling`).
- **Two sorted stop sets.**
  - *Up stops*: up pickups, plus drop-offs above the elevator.
  - *Down stops*: down pickups, plus drop-offs below.
- **Going up from floor f:**
  1. The target is the lowest up stop at or above f. A pickup at f is served now.
  2. If there is none, go to the highest down pickup above f and turn there. That is a down call while moving up,
     and it marks the top of the sweep.
  3. Otherwise reverse.
- Going down is symmetric. When idle, head for the nearest stop.
- LOOK differs from SCAN in that it never runs to the end of the shaft when nothing is there.
- **Interface change.** `ISchedulingPolicy.NextGoal → int?` became `Decide → (Target, ServeDirection)`. LOOK must say
  which lantern to show at its target: at a turning point it serves the opposite direction. FIFO was ported
  unchanged; the v1 golden trace (waits 9/38/63 s) still passes byte for byte.
- **FIFO stays selectable** (`--scheduling fifo`) as the baseline.

## Consequences

Measured in [`docs/results/m2-look.md`](../results/m2-look.md) on the same 20 seeds. Δ is the paired per-seed
difference against `fifo+nearest`; every interval below excludes 0.

| Profile | LOOK+Nearest wait avg | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|
| up-peak | 19.2 ± 0.9 s | −6.7 ± 1.4 s | −42.2 ± 7.6 s | −5.7 ± 1.5 s |
| down-peak | 28.3 ± 0.9 s | −227.3 ± 9.0 s | −507.6 ± 29.6 s | −220.9 ± 9.0 s |
| lunch | 20.7 ± 0.9 s | −78.4 ± 8.1 s | −279.6 ± 33.4 s | −75.7 ± 7.9 s |

- `travel_away` falls from 95.0 s to 4.8 s at down-peak.
- **Ride time can grow.** Riders now share the elevator with pickups made on the way. In `v1-basic` (hand-traced in
  `LookScenarioTests`) the mean wait falls from 36.7 s to 24.3 s and the max from 63 s to 48 s, but P1's ride goes
  from 13 s to 23 s. Journey time (wait + ride) is reported next to wait so this trade-off stays visible; overall it
  improves in every profile.
- **No starvation.** A sweep is bounded, and every stop is reached within at most two sweeps.
- **What LOOK does not fix: lobby crowding under Nearest at up-peak.** Nearest sends every lobby request to the
  same nearby elevator, which fills up. That leaves 5.2 ± 3.6 people per run behind at the door (RoundRobin: 0).
  This motivates the predicted-load term of the cost-based dispatcher (ADR-006).
