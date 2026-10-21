# M3: LOOK+Cost vs LOOK+Nearest (held-out seeds 21-40)

- **Command:** `elevator bench --strategies look+nearest,look+cost --baseline look+nearest --seeds 20 --first-seed 21 --out docs/results/m3-cost-vs-nearest.md --title "M3: LOOK+Cost vs LOOK+Nearest (held-out seeds 21-40)" --capacity-mode defer`
- **Reproducibility:** produced in capacity mode `defer` before the dispatcher's pending-assignment overlay (ADR-006). The command reproduces this file byte for byte at commit `d5864a0`. Later commits differ by at most 0.1 s in the means and 1.2 s in p95/max, because deferred requests retried mid-instant now also see pickups just accepted elsewhere. No conclusion changes.
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** look+nearest
- **Scenarios:** up-peak (4 elevators, 20 floors, capacity 10, defer), down-peak (4 elevators, 20 floors, capacity 10, defer), lunch (4 elevators, 20 floors, capacity 10, defer)
- **Runs:** 120, 0.9 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest (baseline) | 19.3 ± 0.6 | 46.7 ± 2.7 | 119.9 ± 13.5 | 54.5 ± 0.9 | 5.8 ± 2.8 | 100.0 ± 0.0 | – | – | – |
| look+cost | 16.4 ± 0.6 | 39.1 ± 1.3 | 65.9 ± 6.4 | 49.7 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | -2.9 ± 0.7 ✱ | -7.5 ± 2.8 ✱ | -4.8 ± 0.5 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 2.4 | 0.0 | 15.6 | 0.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.3 | 0.0 | 14.4 | 0.3 |

## down-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest (baseline) | 28.3 ± 1.2 | 84.1 ± 3.1 | 144.2 ± 15.7 | 59.1 ± 1.4 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+cost | 20.8 ± 0.7 | 53.3 ± 2.3 | 92.2 ± 6.7 | 49.6 ± 1.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | -7.5 ± 1.0 ✱ | -30.8 ± 3.1 ✱ | -9.5 ± 1.0 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.8 | 6.3 | 0.0 | 16.3 | 4.8 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 3.7 | 0.0 | 14.4 | 2.2 |

## lunch

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest (baseline) | 20.9 ± 1.3 | 79.2 ± 4.5 | 127.5 ± 8.5 | 49.0 ± 1.6 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+cost | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | -7.1 ± 0.8 ✱ | -40.8 ± 4.3 ✱ | -8.2 ± 0.8 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 5.0 | 0.0 | 12.1 | 3.2 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |

