# M2: LOOK vs FIFO

- **Command:** `elevator bench --scheduling fifo,look --dispatch roundrobin,nearest --seeds 20 --out docs/results/m2-look.md --title "M2: LOOK vs FIFO"`
- **Seeds:** 1..20 (20 per strategy)
- **Baseline:** fifo+nearest
- **Scenarios:** up-peak (4 elevators, 20 floors, capacity 10), down-peak (4 elevators, 20 floors, capacity 10), lunch (4 elevators, 20 floors, capacity 10)
- **Runs:** 240, 1.5 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 50.7 ± 2.4 | 126.8 ± 9.1 | 233.5 ± 24.9 | 80.8 ± 2.6 | 0.1 ± 0.1 | 100.0 ± 0.0 | 24.8 ± 2.2 ✱ | 37.4 ± 8.5 ✱ | 20.6 ± 2.3 ✱ |
| fifo+nearest (baseline) | 25.9 ± 2.0 | 89.3 ± 8.6 | 186.7 ± 21.7 | 60.2 ± 2.1 | 8.1 ± 5.5 | 100.0 ± 0.0 | – | – | – |
| look+roundrobin (lower bound) | 39.3 ± 1.0 | 86.6 ± 1.9 | 106.7 ± 1.8 | 69.5 ± 1.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | 13.3 ± 1.9 ✱ | -2.8 ± 7.9 | 9.3 ± 1.9 ✱ |
| look+nearest | 19.2 ± 0.9 | 47.1 ± 3.5 | 116.1 ± 12.7 | 54.5 ± 0.9 | 5.2 ± 3.6 | 100.0 ± 0.0 | -6.7 ± 1.4 ✱ | -42.2 ± 7.6 ✱ | -5.7 ± 1.5 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 9.8 | 0.0 | 28.5 | 11.9 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 3.8 | 0.0 | 18.0 | 3.8 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 7.2 | 0.0 | 23.9 | 7.6 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 2.4 | 0.0 | 15.5 | 0.9 |

## down-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 276.6 ± 9.2 | 591.0 ± 25.8 | 694.3 ± 31.9 | 301.3 ± 9.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | 21.0 ± 12.2 ✱ | -1.5 ± 32.5 | 21.0 ± 12.2 ✱ |
| fifo+nearest (baseline) | 255.6 ± 9.0 | 592.5 ± 30.7 | 729.2 ± 55.2 | 280.3 ± 9.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+roundrobin (lower bound) | 39.7 ± 0.9 | 86.9 ± 1.7 | 116.0 ± 9.1 | 70.1 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | -215.9 ± 9.2 ✱ | -505.6 ± 30.6 ✱ | -210.2 ± 9.1 ✱ |
| look+nearest | 28.3 ± 0.9 | 84.9 ± 2.4 | 144.5 ± 18.5 | 59.4 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | -227.3 ± 9.0 ✱ | -507.6 ± 29.6 ✱ | -220.9 ± 9.0 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 55.8 | 0.0 | 117.5 | 102.8 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 52.7 | 0.0 | 107.3 | 95.0 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 8.4 | 0.0 | 22.7 | 8.0 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.8 | 6.2 | 0.0 | 16.4 | 4.8 |

## lunch

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 123.9 ± 8.3 | 349.0 ± 25.8 | 474.1 ± 39.9 | 148.8 ± 8.5 | 0.0 ± 0.0 | 100.0 ± 0.0 | 24.8 ± 7.5 ✱ | -4.5 ± 31.9 | 24.0 ± 7.4 ✱ |
| fifo+nearest (baseline) | 99.1 ± 8.3 | 353.5 ± 34.2 | 529.4 ± 65.7 | 124.7 ± 8.3 | 0.1 ± 0.1 | 100.0 ± 0.0 | – | – | – |
| look+roundrobin (lower bound) | 37.6 ± 1.1 | 86.0 ± 2.0 | 107.7 ± 3.1 | 64.5 ± 1.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | -61.5 ± 7.8 ✱ | -267.4 ± 33.3 ✱ | -60.2 ± 7.7 ✱ |
| look+nearest | 20.7 ± 0.9 | 73.9 ± 3.9 | 120.1 ± 7.5 | 49.0 ± 1.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | -78.4 ± 8.1 ✱ | -279.6 ± 33.4 ✱ | -75.7 ± 7.9 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 26.9 | 0.0 | 55.6 | 40.9 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 22.6 | 0.0 | 43.0 | 32.9 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 8.1 | 0.0 | 21.9 | 6.9 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 4.9 | 0.0 | 12.0 | 3.0 |

