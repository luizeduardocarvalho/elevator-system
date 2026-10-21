# M5: defer vs next-trip under full load (held-out seeds 21-40)

- **Command:** `elevator bench --strategies look+nearest,look+cost --capacity-mode defer,next-trip --baseline look+cost/defer --seeds 20 --first-seed 21 --out docs/results/m5-capacity-modes.md --title "M5: defer vs next-trip under full load (held-out seeds 21-40)"`
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** look+cost/defer
- **Scenarios:** up-peak (4 elevators, 20 floors, capacity 10, next-trip), down-peak (4 elevators, 20 floors, capacity 10, next-trip), lunch (4 elevators, 20 floors, capacity 10, next-trip)
- **Runs:** 240, 5.9 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest/defer | 19.3 ± 0.6 | 46.6 ± 2.7 | 119.5 ± 13.6 | 54.5 ± 0.9 | 5.8 ± 2.8 | 100.0 ± 0.0 | 2.9 ± 0.7 ✱ | 7.4 ± 2.8 ✱ | 4.7 ± 0.5 ✱ |
| look+nearest/next-trip | 20.7 ± 1.1 | 59.4 ± 9.4 | 146.7 ± 14.4 | 55.9 ± 1.2 | 4.8 ± 2.1 | 100.0 ± 0.0 | 4.4 ± 1.2 ✱ | 20.3 ± 9.1 ✱ | 6.2 ± 1.1 ✱ |
| look+cost/defer (baseline) | 16.4 ± 0.6 | 39.1 ± 1.3 | 65.9 ± 6.4 | 49.7 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+cost/next-trip | 15.8 ± 0.6 | 37.9 ± 0.8 | 57.6 ± 6.4 | 49.3 ± 0.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | -0.6 ± 0.4 ✱ | -1.2 ± 1.4 | -0.4 ± 0.3 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest/defer | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 2.4 | 0.0 | 15.6 | 0.9 |
| look+nearest/next-trip | 0.0 | 0.0 | 0.5 | 0.0 | 0.4 | 3.1 | 0.0 | 15.3 | 1.4 |
| look+cost/defer | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.3 | 0.0 | 14.4 | 0.3 |
| look+cost/next-trip | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.2 | 0.0 | 14.0 | 0.2 |

## down-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest/defer | 28.3 ± 1.2 | 84.1 ± 3.1 | 144.2 ± 15.7 | 59.1 ± 1.4 | 0.0 ± 0.0 | 100.0 ± 0.0 | 7.5 ± 1.0 ✱ | 30.8 ± 3.1 ✱ | 9.5 ± 1.0 ✱ |
| look+nearest/next-trip | 29.3 ± 1.1 | 87.7 ± 3.7 | 169.2 ± 24.4 | 60.2 ± 1.4 | 0.1 ± 0.1 | 100.0 ± 0.0 | 8.5 ± 0.9 ✱ | 34.4 ± 4.1 ✱ | 10.6 ± 0.9 ✱ |
| look+cost/defer (baseline) | 20.8 ± 0.7 | 53.3 ± 2.3 | 92.2 ± 6.7 | 49.6 ± 1.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+cost/next-trip | 20.6 ± 0.7 | 53.2 ± 2.1 | 94.0 ± 7.4 | 49.4 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | -0.2 ± 0.2 | -0.1 ± 0.9 | -0.2 ± 0.2 |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest/defer | 0.0 | 0.0 | 0.0 | 0.0 | 0.8 | 6.3 | 0.0 | 16.3 | 4.8 |
| look+nearest/next-trip | 0.0 | 0.0 | 0.4 | 0.0 | 0.8 | 6.6 | 0.0 | 16.7 | 4.9 |
| look+cost/defer | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 3.7 | 0.0 | 14.4 | 2.2 |
| look+cost/next-trip | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 3.6 | 0.0 | 14.2 | 2.1 |

## lunch

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest/defer | 20.9 ± 1.3 | 79.2 ± 4.5 | 127.5 ± 8.5 | 49.0 ± 1.6 | 0.0 ± 0.0 | 100.0 ± 0.0 | 7.1 ± 0.8 ✱ | 40.8 ± 4.3 ✱ | 8.2 ± 0.8 ✱ |
| look+nearest/next-trip | 21.0 ± 1.3 | 78.1 ± 4.3 | 127.8 ± 8.4 | 49.1 ± 1.6 | 0.0 ± 0.0 | 100.0 ± 0.0 | 7.2 ± 0.8 ✱ | 39.6 ± 3.8 ✱ | 8.3 ± 0.9 ✱ |
| look+cost/defer (baseline) | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+cost/next-trip | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | 0.0 ± 0.0 | 0.0 ± 0.0 | 0.0 ± 0.0 |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+nearest/defer | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 5.0 | 0.0 | 12.1 | 3.2 |
| look+nearest/next-trip | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 5.1 | 0.0 | 12.1 | 3.1 |
| look+cost/defer | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |
| look+cost/next-trip | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |

