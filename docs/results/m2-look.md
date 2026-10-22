# M2: LOOK vs FIFO

- **Command:** `elevator bench --scheduling fifo,look --dispatch roundrobin,nearest --seeds 20 --out docs/results/m2-look.md --title "M2: LOOK vs FIFO" --capacity-mode defer`
- **Seeds:** 1..20 (20 per strategy)
- **Baseline:** fifo+nearest
- **Scenarios:** up-peak (4 elevators, 20 floors, capacity 10, defer), down-peak (4 elevators, 20 floors, capacity 10, defer), lunch (4 elevators, 20 floors, capacity 10, defer)
- **Runs:** 240, 7.4 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 50.6 ± 2.3 | 126.0 ± 8.9 | 241.5 ± 37.9 | 80.6 ± 2.5 | 0.1 ± 0.1 | 100.0 ± 0.0 | 24.4 ± 2.2 ✱ | 36.1 ± 9.1 ✱ | 20.2 ± 2.3 ✱ |
| fifo+nearest (baseline) | 26.1 ± 2.1 | 89.8 ± 8.8 | 189.4 ± 21.6 | 60.5 ± 2.2 | 8.8 ± 6.3 | 100.0 ± 0.0 | – | – | – |
| look+roundrobin (lower bound) | 39.3 ± 1.0 | 86.6 ± 1.9 | 106.7 ± 1.8 | 69.5 ± 1.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | 13.1 ± 2.0 ✱ | -3.3 ± 8.2 | 9.1 ± 2.0 ✱ |
| look+nearest | 19.2 ± 0.9 | 47.1 ± 3.5 | 116.1 ± 12.7 | 54.5 ± 0.9 | 5.2 ± 3.6 | 100.0 ± 0.0 | -6.9 ± 1.5 ✱ | -42.8 ± 7.8 ✱ | -5.9 ± 1.6 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 9.8 | 0.0 | 28.4 | 11.9 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 3.9 | 0.0 | 18.0 | 3.8 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 7.2 | 0.0 | 23.9 | 7.6 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 2.4 | 0.0 | 15.5 | 0.9 |

## down-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 276.6 ± 9.2 | 591.0 ± 25.8 | 694.3 ± 31.9 | 301.3 ± 9.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | 21.0 ± 12.2 ✱ | -1.5 ± 32.5 | 21.0 ± 12.2 ✱ |
| fifo+nearest (baseline) | 255.6 ± 9.0 | 592.5 ± 30.7 | 729.2 ± 55.2 | 280.3 ± 9.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+roundrobin (lower bound) | 39.8 ± 0.9 | 87.0 ± 1.8 | 118.6 ± 10.3 | 70.2 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | -215.8 ± 9.2 ✱ | -505.5 ± 30.7 ✱ | -210.1 ± 9.1 ✱ |
| look+nearest | 28.2 ± 0.9 | 84.5 ± 2.7 | 144.4 ± 18.6 | 59.3 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | -227.4 ± 9.0 ✱ | -508.0 ± 29.5 ✱ | -221.0 ± 9.0 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 55.8 | 0.0 | 117.5 | 102.8 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 52.7 | 0.0 | 107.3 | 95.0 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 8.5 | 0.0 | 22.7 | 8.0 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.8 | 6.2 | 0.0 | 16.4 | 4.8 |

## lunch

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 123.9 ± 8.3 | 349.0 ± 25.8 | 474.1 ± 39.9 | 148.8 ± 8.5 | 0.0 ± 0.0 | 100.0 ± 0.0 | 25.1 ± 7.4 ✱ | -3.3 ± 32.0 | 24.4 ± 7.4 ✱ |
| fifo+nearest (baseline) | 98.8 ± 8.2 | 352.3 ± 34.2 | 518.0 ± 56.2 | 124.4 ± 8.2 | 0.1 ± 0.1 | 100.0 ± 0.0 | – | – | – |
| look+roundrobin (lower bound) | 37.6 ± 1.1 | 86.0 ± 2.0 | 107.7 ± 3.1 | 64.5 ± 1.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | -61.2 ± 7.7 ✱ | -266.2 ± 33.3 ✱ | -59.9 ± 7.6 ✱ |
| look+nearest | 20.7 ± 0.9 | 73.9 ± 3.9 | 120.1 ± 7.5 | 49.0 ± 1.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | -78.1 ± 8.0 ✱ | -278.4 ± 33.3 ✱ | -75.4 ± 7.8 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 26.9 | 0.0 | 55.6 | 40.9 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 22.5 | 0.0 | 42.9 | 32.8 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 8.1 | 0.0 | 21.9 | 6.9 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 4.9 | 0.0 | 12.0 | 3.0 |

