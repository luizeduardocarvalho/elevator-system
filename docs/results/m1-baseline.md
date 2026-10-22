# M1 baseline: FIFO before any smarter strategy

- **Command:** `elevator bench --scheduling fifo --dispatch roundrobin,nearest --seeds 20 --out docs/results/m1-baseline.md --title "M1 baseline: FIFO before any smarter strategy" --capacity-mode defer`
- **Seeds:** 1..20 (20 per strategy)
- **Baseline:** fifo+nearest
- **Scenarios:** up-peak (4 elevators, 20 floors, capacity 10, defer), down-peak (4 elevators, 20 floors, capacity 10, defer), lunch (4 elevators, 20 floors, capacity 10, defer)
- **Runs:** 120, 6.5 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 50.6 ± 2.3 | 126.0 ± 8.9 | 241.5 ± 37.9 | 80.6 ± 2.5 | 0.1 ± 0.1 | 100.0 ± 0.0 | 24.4 ± 2.2 ✱ | 36.1 ± 9.1 ✱ | 20.2 ± 2.3 ✱ |
| fifo+nearest (baseline) | 26.1 ± 2.1 | 89.8 ± 8.8 | 189.4 ± 21.6 | 60.5 ± 2.2 | 8.8 ± 6.3 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 9.8 | 0.0 | 28.4 | 11.9 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 3.9 | 0.0 | 18.0 | 3.8 |

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
| fifo+roundrobin (lower bound) | 123.9 ± 8.3 | 349.0 ± 25.8 | 474.1 ± 39.9 | 148.8 ± 8.5 | 0.0 ± 0.0 | 100.0 ± 0.0 | 25.1 ± 7.4 ✱ | -3.3 ± 32.0 | 24.4 ± 7.4 ✱ |
| fifo+nearest (baseline) | 98.8 ± 8.2 | 352.3 ± 34.2 | 518.0 ± 56.2 | 124.4 ± 8.2 | 0.1 ± 0.1 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 26.9 | 0.0 | 55.6 | 40.9 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 22.5 | 0.0 | 42.9 | 32.8 |

