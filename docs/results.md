# Results

Every number here comes from a file in [`docs/results/`](results/) that records the exact command that produced it.
Simulation results are deterministic (same command → byte-identical file). Wall-clock results depend on the machine
and its load; they are labelled with the machine and were taken on a quiet one.

## 1. The experiment, step by step (simulation, sim seconds)

Average wait / p95 wait for 20 seeds per cell, paired on common random numbers.
- Building: 20 floors, 4 elevators, capacity 10, 12 passengers/min at peak.
- Seeds: 1–20 for exploring and tuning, 21–40 (held out) for the reported decisions.

| Step | File | Up-peak | Down-peak | Lunch |
|---|---|---|---|---|
| Baseline: FIFO + Nearest, measured before any change | [m1-baseline](results/m1-baseline.md) | 25.9 / 89.3 | 255.6 / 592.5 (saturated) | 99.1 / 353.5 |
| + LOOK | [m2-look](results/m2-look.md) | 19.2 / 47.1 | 28.3 / 84.9 | 20.7 / 73.9 |
| + cost-based dispatch (held-out) | [m3-cost](results/m3-cost.md) | 16.4 / 39.1 | 20.8 / 53.3 | 13.8 / 38.4 |
| ± rebalancing (held-out) | [m4-rebalance](results/m4-rebalance.md) | −0.2 s (n.s.) | −0.9 ± 0.5 s | −0.4 ± 0.4 s |
| next-trip capacity, final default (held-out) | [m5-next-trip](results/m5-next-trip.md) | **15.8 / 37.9** | **20.6 / 53.2** | **13.8 / 38.4** |

Supporting files:
- [m3-cost-weights](results/m3-cost-weights.md): the λ, μ sweep;
- [m3-cost-vs-nearest](results/m3-cost-vs-nearest.md): paired against LOOK + Nearest;
- [m5-capacity-modes](results/m5-capacity-modes.md): defer vs next-trip.

M1–M4 were produced in capacity mode `defer` (ADR-010) and say so in their headers.

## 2. The cost of a dispatch decision (BenchmarkDotNet)

One cost-based decision for 4 elevators on 20 floors, each holding N requests not yet picked up. Every call builds a
fresh dispatcher, so nothing is cached (worst case, as in a burst). Machine: Apple M2 Pro (10 cores, 16 GB), macOS 26.5, .NET 10.

| Backlog per elevator | Before: per-passenger projection | After: per-stop projection | Allocated before → after |
|---|---|---|---|
| 0 | 22.5 µs | **2.1 µs** | 97 KB → 17 KB |
| 25 | 337 µs | **33 µs** | 976 KB → 97 KB |
| 100 | 778 µs | **64 µs** | 1.8 MB → 153 KB |
| 400 | 2,327 µs | **80 µs** | 3.9 MB → 188 KB |
| 1,600 | (grows further) | **88 µs** (flat) | → 201 KB |

Nearest, for reference: 0.13–1.4 µs. Full reports:
[before](results/benchmarks/dispatch-decision-before.md), [after](results/benchmarks/dispatch-decision-after.md).

**Why the old projection grew with the backlog.** It was not the scheduling policy: policy calls were bounded at
about 850 per decision ([anatomy](results/benchmarks/projection-anatomy.txt)). The growth was per-passenger work
inside the projection:
- each rider boarded one at a time;
- every door opening scanned all riders;
- request lists were copied;
- a per-passenger completion map was built and summed.

**What changed.**
- **Per-stop, per-destination aggregates**, maintained by the elevator.
- **ΔD by prefix matching.** Inside a stop, boarding order is arrival order and the new request is last, so in both
  projections the people who board are a prefix of the same list.
- **Bitmask decisions.** LOOK and FIFO gained an O(1) bitmask `Decide`.
- **Stop-to-stop travel.** Once nothing else can change, the projection moves from stop to stop instead of floor by
  floor.

The result is O(floors²) per elevator with small constants and **independent of the backlog**. The new projection
equals the old one exactly on 1,500 random elevators (differential property), and every results file regenerated
byte-identically.

## 3. Performance under load (real clock, real threads)

_To be filled from a quiet-machine run: latency curves, pipeline breakdown, burst capacity, SLO table._
