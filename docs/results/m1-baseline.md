# M1 baseline: FIFO before any smarter strategy

- **Command:** `elevator bench --scheduling fifo --dispatch roundrobin,nearest --seeds 20 --out docs/results/m1-baseline.md --title "M1 baseline: FIFO before any smarter strategy" --capacity-mode defer`
- **Reproducibility:** produced in capacity mode `defer` before the dispatcher's pending-assignment overlay (ADR-006). The command reproduces this file byte for byte at commit `d5864a0`. Later commits differ by at most 0.1 s in the means and 1.2 s in p95/max, because deferred requests retried mid-instant now also see pickups just accepted elsewhere. No conclusion changes.
- **Seeds:** 1..20 (20 per strategy)
- **Baseline:** fifo+nearest
- **Scenarios:** up-peak (4 elevators, 20 floors, capacity 10, defer), down-peak (4 elevators, 20 floors, capacity 10, defer), lunch (4 elevators, 20 floors, capacity 10, defer)
- **Runs:** 120, 0.8 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 50.7 ± 2.4 | 126.8 ± 9.1 | 233.5 ± 24.9 | 80.8 ± 2.6 | 0.1 ± 0.1 | 100.0 ± 0.0 | 24.8 ± 2.2 ✱ | 37.4 ± 8.5 ✱ | 20.6 ± 2.3 ✱ |
| fifo+nearest (baseline) | 25.9 ± 2.0 | 89.3 ± 8.6 | 186.7 ± 21.7 | 60.2 ± 2.1 | 8.1 ± 5.5 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 9.8 | 0.0 | 28.5 | 11.9 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 3.8 | 0.0 | 18.0 | 3.8 |

## down-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 276.6 ± 9.2 | 591.0 ± 25.8 | 694.3 ± 31.9 | 301.3 ± 9.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | 21.0 ± 12.2 ✱ | -1.5 ± 32.5 | 21.0 ± 12.2 ✱ |
| fifo+nearest (baseline) | 255.6 ± 9.0 | 592.5 ± 30.7 | 729.2 ± 55.2 | 280.3 ± 9.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 55.8 | 0.0 | 117.5 | 102.8 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 52.7 | 0.0 | 107.3 | 95.0 |

## lunch

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 123.9 ± 8.3 | 349.0 ± 25.8 | 474.1 ± 39.9 | 148.8 ± 8.5 | 0.0 ± 0.0 | 100.0 ± 0.0 | 24.8 ± 7.5 ✱ | -4.5 ± 31.9 | 24.0 ± 7.4 ✱ |
| fifo+nearest (baseline) | 99.1 ± 8.3 | 353.5 ± 34.2 | 529.4 ± 65.7 | 124.7 ± 8.3 | 0.1 ± 0.1 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 26.9 | 0.0 | 55.6 | 40.9 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 22.6 | 0.0 | 43.0 | 32.9 |

