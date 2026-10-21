# M4: sticky vs rebalance (held-out seeds 21-40)

- **Command:** `elevator bench --strategies look+cost,look+cost+rebalance,look+nearest,look+nearest+rebalance --baseline look+cost --seeds 20 --first-seed 21 --out docs/results/m4-rebalance.md --title "M4: sticky vs rebalance (held-out seeds 21-40)" --out out/regen/m4-new.md`
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** look+cost
- **Scenarios:** up-peak (4 elevators, 20 floors, capacity 10), down-peak (4 elevators, 20 floors, capacity 10), lunch (4 elevators, 20 floors, capacity 10)
- **Runs:** 240, 4.8 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+cost (baseline) | 16.4 ± 0.6 | 39.1 ± 1.3 | 65.9 ± 6.4 | 49.7 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+cost+rebalance | 16.2 ± 0.6 | 39.2 ± 1.4 | 61.4 ± 5.1 | 49.6 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | -0.2 ± 0.4 | 0.1 ± 0.8 | -0.1 ± 0.3 |
| look+nearest | 19.3 ± 0.6 | 46.7 ± 2.7 | 119.9 ± 13.5 | 54.5 ± 0.9 | 5.8 ± 2.8 | 100.0 ± 0.0 | 2.9 ± 0.7 ✱ | 7.5 ± 2.8 ✱ | 4.8 ± 0.5 ✱ |
| look+nearest+rebalance | 17.5 ± 0.7 | 41.9 ± 1.7 | 89.7 ± 20.8 | 52.7 ± 0.9 | 4.8 ± 1.9 | 100.0 ± 0.0 | 1.1 ± 0.8 ✱ | 2.8 ± 1.9 ✱ | 3.0 ± 0.8 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.3 | 0.0 | 14.4 | 0.3 |
| look+cost+rebalance | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.3 | 0.0 | 14.3 | 0.3 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 2.4 | 0.0 | 15.6 | 0.9 |
| look+nearest+rebalance | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 1.8 | 0.0 | 14.7 | 0.5 |

## down-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+cost (baseline) | 20.8 ± 0.7 | 53.3 ± 2.3 | 92.2 ± 6.7 | 49.6 ± 1.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+cost+rebalance | 19.9 ± 0.8 | 49.2 ± 2.0 | 74.0 ± 5.7 | 48.7 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | -0.9 ± 0.5 ✱ | -4.2 ± 1.9 ✱ | -0.9 ± 0.4 ✱ |
| look+nearest | 28.3 ± 1.2 | 84.1 ± 3.1 | 144.2 ± 15.7 | 59.1 ± 1.4 | 0.0 ± 0.0 | 100.0 ± 0.0 | 7.5 ± 1.0 ✱ | 30.8 ± 3.1 ✱ | 9.5 ± 1.0 ✱ |
| look+nearest+rebalance | 22.7 ± 0.7 | 58.9 ± 3.0 | 113.3 ± 11.5 | 53.0 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | 1.9 ± 0.9 ✱ | 5.6 ± 3.3 ✱ | 3.4 ± 0.9 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 3.7 | 0.0 | 14.4 | 2.2 |
| look+cost+rebalance | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 3.5 | 0.0 | 14.0 | 1.9 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.8 | 6.3 | 0.0 | 16.3 | 4.8 |
| look+nearest+rebalance | 0.0 | 0.0 | 0.0 | 0.0 | 1.3 | 4.5 | 0.0 | 14.5 | 2.5 |

## lunch

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+cost (baseline) | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+cost+rebalance | 13.4 ± 0.8 | 37.2 ± 2.4 | 62.7 ± 5.8 | 40.3 ± 1.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | -0.4 ± 0.4 ✱ | -1.2 ± 1.7 | -0.5 ± 0.4 ✱ |
| look+nearest | 20.9 ± 1.3 | 79.2 ± 4.5 | 127.5 ± 8.5 | 49.0 ± 1.6 | 0.0 ± 0.0 | 100.0 ± 0.0 | 7.1 ± 0.8 ✱ | 40.8 ± 4.3 ✱ | 8.2 ± 0.8 ✱ |
| look+nearest+rebalance | 15.3 ± 1.0 | 44.9 ± 2.8 | 91.0 ± 7.3 | 43.3 ± 1.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | 1.5 ± 1.0 ✱ | 6.5 ± 3.6 ✱ | 2.5 ± 1.0 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |
| look+cost+rebalance | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.3 | 0.0 | 9.8 | 0.7 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 5.0 | 0.0 | 12.1 | 3.2 |
| look+nearest+rebalance | 0.0 | 0.0 | 0.0 | 0.0 | 0.9 | 3.3 | 0.0 | 9.8 | 1.3 |

