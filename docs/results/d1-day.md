# D1: a working day, 07:00-19:00 (held-out seeds 21-40)

- **Command:** `elevator bench --scenarios day --strategies fifo+nearest,look+nearest,look+cost --baseline look+cost --seeds 20 --first-seed 21 --out docs/results/d1-day.md --title "D1: a working day, 07:00-19:00 (held-out seeds 21-40)"`
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** look+cost
- **Scenarios:** day (4 elevators, 20 floors, capacity 10, next-trip)
- **Runs:** 60, 4.3 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## day

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 114.8 ± 2.4 | 532.8 ± 7.8 | 876.2 ± 39.3 | 142.2 ± 2.5 | 16.1 ± 5.1 | 100.0 ± 0.0 | 99.2 ± 2.4 ✱ | 491.3 ± 8.0 ✱ | 97.8 ± 2.4 ✱ |
| look+nearest | 21.0 ± 0.4 | 70.0 ± 1.8 | 248.7 ± 22.7 | 51.0 ± 0.5 | 14.3 ± 5.3 | 100.0 ± 0.0 | 5.3 ± 0.4 ✱ | 28.5 ± 1.9 ✱ | 6.6 ± 0.4 ✱ |
| look+cost (baseline) | 15.7 ± 0.1 | 41.5 ± 0.6 | 120.2 ± 13.7 | 44.4 ± 0.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.3 | 0.0 | 0.4 | 23.6 | 0.0 | 51.1 | 39.4 |
| look+nearest | 0.0 | 0.0 | 0.3 | 0.0 | 0.5 | 3.6 | 0.0 | 14.1 | 2.5 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.8 | 0.0 | 12.6 | 0.9 |

