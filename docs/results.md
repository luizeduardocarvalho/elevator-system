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

### Hard stage (types, access, faults; held-out seeds 21–40)

| Question | File | Answer |
|---|---|---|
| What does the chaos schedule cost (same passengers, paired)? | [h1-chaos](results/h1-chaos.md) | LOOK + Cost: wait 13.8 → 19.6 s (+5.8 ± 0.8), p95 +20.5 ± 3.3 s, 100 % served; half the pickups handed back of LOOK + Nearest |
| Does cost dispatch use elevator types? | [h2-fleet](results/h2-fleet.md) | typed fleet: Cost 14.0 / 39.8 vs Nearest 22.8 / 81.9 (Δ −8.8 ± 1.0 s avg) |
| Watchdog timeout T: detection vs penalty | [h3-watchdog-timeout](results/h3-watchdog-timeout.md) | detection ≈ T; penalty 9.5 → 129 s per run from T = 5 to 60 s; average wait flat |
| Watchdog false positives on fault-free runs | [h4-watchdog-false-positives](results/h4-watchdog-false-positives.md) | ≈ 61–65 per run at T = 5 s on the typed fleet (6 s freight dwell); none at 10 or 30 s |

The v2 results above regenerate byte-identically with the Hard stage in place (faults off, default config).

### A working day (07:00–19:00, held-out seeds 21–40)

| Question | File | Answer |
|---|---|---|
| How does each strategy cope with a whole day? | [d1-day](results/d1-day.md) | wait avg / p95: FIFO + Nearest 114.8 / 532.8 s, LOOK + Nearest 21.0 / 70.0 s, LOOK + Cost 15.7 / 41.5 s; nobody left behind under Cost |
| Where and when do people wait? | [d1-day-heatmaps](results/d1-day-heatmaps.md) | evening down-peak and the lunch run strand the upper floors; the morning up-peak barely shows as wait (it costs ride time instead) |
| Where in the day does Cost beat Nearest? (difference map, seed 1) | [d1-day-heatmaps](results/d1-day-heatmaps.md#difference-lookcost--looknearest) | −6.2 s per passenger; less wait in 286 cells, more in 76; largest gains on floors 3–8 at lunch and in the evening (full elevators from above pass them under Nearest) |

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

Measured on the M2 Pro after the machine had quietened down (1-minute load average under 3; it had been 8–75 during
Xcode builds). Default system: LOOK + cost dispatch, next-trip capacity, overload fallback to Nearest above 200
requests in flight, realistic capacity (10). Latency is wall clock from `AssignRequest` to the accepted assignment,
queueing included.

### Spec SLOs ([perf](results/perf.md))

| SLO | Measured | Result |
|---|---|---|
| Assignment p99 < 100 ms at 100 concurrent (spec: "100+") | 12.2 ms | PASS |
| Assignment p99 < 100 ms at 1,000 concurrent | 53.6 ms (800 of 1,000 decided by the fallback) | PASS |
| Assignment p99 < 100 ms at 10,000 concurrent (stretch) | 201.9 ms | FAIL |
| Every request assigned (100 / 1k / 10k) | 100 / 1,000 / 10,000 | PASS |
| Lost = 0, duplicated = 0 | 0, 0 | PASS |
| Memory: heap growth < 10% over a 60 s soak | +0.9% (1.06 → 1.10 MB; 1,183 of 1,190 delivered) | PASS |

The perf test gate (1,000 submitters, then a 20 s soak) passed 3 runs out of 3.

### Latency vs burst size ([perf-curves](results/perf-curves.md))

Each point is a fresh process with warm-up, median p99 of 3 repeats.

| Simultaneous submitters | 100 | 300 | 1k | 3k | 5k | 6.5k | 8k | 10k |
|---|---|---|---|---|---|---|---|---|
| Default (cost + fallback), p99 ms | 12.7 | 38.1 | 50.6 | 80.4 | 103.3 | 125.4 | 158.8 | 181.6 |
| Cost only, p99 ms | 11.6 | 66.0 | 309.7 | 605.8 | 852.8 | 858.5 | 989.6 | 1,131.3 |
| Nearest only, p99 ms | 1.1 | 3.2 | 18.5 | 49.5 | 77.2 | 101.9 | 120.9 | 158.0 |

Nothing was lost or duplicated at any point.

**Reading the curves.**
- *Cost alone* answers within the SLO up to a few hundred simultaneous requests. Beyond that it is CPU-bound at about
  3–9k decisions/s: a projection costs tens of µs (§2), and the dispatcher is the one serial stage.
- *The overload fallback* hands decisions to Nearest above 200 in flight. That is what keeps the default under
  100 ms to about 4.5k simultaneous requests.
- *Bench quality is unaffected* because normal traffic never gets near 200 in flight (peak 21 for LOOK).

### Burst capacity (the headline)

**About 4.5k simultaneous requests within p99 < 100 ms with the default system, about 6.4k with Nearest alone.**
That is roughly 45× and 64× the spec's "100+".

The arithmetic:
- *The ceiling.* The dispatcher is the single serial stage. Nearest's pipeline drains about 57k requests/s (55–59k
  measured from 3k up), so a burst of N waits about N / 57k s for the last request: 100 ms ↔ ≈ 5.7k. Fixed overheads
  bring the measured crossing to between 5k (77 ms) and 6.5k (102 ms).
- *10k simultaneous* needs ≈ 175 ms at that rate, hence the stretch FAIL (158 ms Nearest, 182 ms default).
- *Scale note.* 10k simultaneous button presses is more than the population of a 20-floor office building (about
  1,500–2,000 people). The stretch level tests the software's limit, not a load the building can generate.

### Where a request's time goes (Nearest, 10k burst, [perf-nearest](results/perf-nearest.md))

| Actor | Messages per request | Handler µs / request | Delivery µs / request |
|---|---|---|---|
| Dispatcher (serial) | 3 (submit, accept, snapshot) | 13.8 | 2.8 |
| Elevators (4 in parallel) | 1 (assign) | 7.0 | 4.7 |
| Passengers | 2 (waiting, accepted) | 0.9 | 2.0 |

- *The decision is not the cost.* The Nearest decision itself is about 1.4 µs (§2). The rest of the dispatcher's
  16.6 µs per request is protocol work:
  - ingesting a snapshot per accepted assignment;
  - bookkeeping (pending set, overlay, events);
  - allocation;
  - channel writes.
- *The resulting bound.* 1 / 16.6 µs ≈ 60k requests/s, against 55–59k measured.

**Next steps if more were needed.** Not done; the spec is met by a wide margin.
- **Shard the dispatcher per elevator bank**, not per zone. Zoning one bank would break the group optimisation the
  cost function exists for; banks are independent by construction.
- **Batch assignment.** Drain the channel in batches and assign each batch as an assignment problem (Hungarian
  algorithm, or grouping by destination). That raises throughput (one snapshot ingestion per batch) and can also
  improve quality, since requests are no longer assigned greedily one at a time.
