# M3: cost-based dispatch, full grid (held-out seeds 21-40)

- **Command:** `elevator bench --scheduling fifo,look --dispatch roundrobin,nearest,cost --seeds 20 --first-seed 21 --out docs/results/m3-cost.md --title "M3: cost-based dispatch, full grid (held-out seeds 21-40)" --capacity-mode defer`
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** fifo+nearest
- **Scenarios:** up-peak (4 elevators, 20 floors, capacity 10, defer), down-peak (4 elevators, 20 floors, capacity 10, defer), lunch (4 elevators, 20 floors, capacity 10, defer)
- **Runs:** 360, 1.8 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 50.0 ± 2.5 | 123.2 ± 9.0 | 236.7 ± 27.2 | 80.0 ± 2.9 | 0.1 ± 0.2 | 100.0 ± 0.0 | 23.6 ± 3.1 ✱ | 35.2 ± 10.8 ✱ | 19.3 ± 3.2 ✱ |
| fifo+nearest (baseline) | 26.4 ± 2.0 | 88.0 ± 10.4 | 203.8 ± 26.5 | 60.6 ± 2.0 | 6.4 ± 3.0 | 100.0 ± 0.0 | – | – | – |
| fifo+cost | 17.9 ± 0.7 | 43.0 ± 2.4 | 93.3 ± 13.8 | 50.3 ± 1.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | -8.4 ± 2.1 ✱ | -45.0 ± 10.4 ✱ | -10.3 ± 2.2 ✱ |
| look+roundrobin (lower bound) | 39.0 ± 1.0 | 86.6 ± 2.1 | 113.1 ± 4.4 | 69.4 ± 1.5 | 0.0 ± 0.0 | 100.0 ± 0.0 | 12.7 ± 2.4 ✱ | -1.4 ± 10.5 | 8.7 ± 2.4 ✱ |
| look+nearest | 19.3 ± 0.6 | 46.7 ± 2.7 | 119.9 ± 13.5 | 54.5 ± 0.9 | 5.8 ± 2.8 | 100.0 ± 0.0 | -7.1 ± 1.8 ✱ | -41.3 ± 10.2 ✱ | -6.2 ± 1.8 ✱ |
| look+cost | 16.4 ± 0.6 | 39.1 ± 1.3 | 65.9 ± 6.4 | 49.7 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | -10.0 ± 2.0 ✱ | -48.9 ± 10.4 ✱ | -10.9 ± 1.9 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 9.7 | 0.0 | 28.2 | 11.6 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 4.0 | 0.0 | 18.0 | 4.0 |
| fifo+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.6 | 0.0 | 15.1 | 0.9 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 7.1 | 0.0 | 23.8 | 7.5 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.4 | 2.4 | 0.0 | 15.6 | 0.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.3 | 0.0 | 14.4 | 0.3 |

## down-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 273.9 ± 13.2 | 589.6 ± 30.1 | 683.2 ± 36.7 | 298.5 ± 13.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | 22.7 ± 9.3 ✱ | 4.9 ± 28.1 | 22.7 ± 9.3 ✱ |
| fifo+nearest (baseline) | 251.1 ± 13.8 | 584.6 ± 33.1 | 718.6 ± 40.1 | 275.8 ± 13.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| fifo+cost | 78.7 ± 3.3 | 169.0 ± 7.2 | 233.0 ± 17.5 | 103.5 ± 3.4 | 0.0 ± 0.0 | 100.0 ± 0.0 | -172.5 ± 11.9 ✱ | -415.6 ± 29.2 ✱ | -172.4 ± 11.9 ✱ |
| look+roundrobin (lower bound) | 40.5 ± 1.3 | 88.3 ± 2.1 | 115.5 ± 8.4 | 70.8 ± 1.7 | 0.0 ± 0.0 | 100.0 ± 0.0 | -210.6 ± 13.2 ✱ | -496.4 ± 31.9 ✱ | -205.0 ± 12.9 ✱ |
| look+nearest | 28.3 ± 1.2 | 84.1 ± 3.1 | 144.2 ± 15.7 | 59.1 ± 1.4 | 0.0 ± 0.0 | 100.0 ± 0.0 | -222.9 ± 13.3 ✱ | -500.6 ± 32.5 ✱ | -216.7 ± 13.2 ✱ |
| look+cost | 20.8 ± 0.7 | 53.3 ± 2.3 | 92.2 ± 6.7 | 49.6 ± 1.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | -230.4 ± 13.5 ✱ | -531.3 ± 32.6 ✱ | -226.2 ± 13.3 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 55.3 | 0.0 | 116.3 | 101.7 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 52.0 | 0.0 | 105.5 | 93.0 |
| fifo+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 15.8 | 0.0 | 38.3 | 24.1 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 8.7 | 0.0 | 22.9 | 8.3 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.8 | 6.3 | 0.0 | 16.3 | 4.8 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 3.7 | 0.0 | 14.4 | 2.2 |

## lunch

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 121.7 ± 11.5 | 357.0 ± 34.6 | 483.1 ± 39.2 | 146.4 ± 11.6 | 0.0 ± 0.0 | 100.0 ± 0.0 | 26.4 ± 7.6 ✱ | 32.0 ± 28.1 ✱ | 25.8 ± 7.6 ✱ |
| fifo+nearest (baseline) | 95.2 ± 12.2 | 325.0 ± 35.0 | 457.4 ± 48.1 | 120.6 ± 12.4 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| fifo+cost | 35.5 ± 4.2 | 112.5 ± 13.7 | 197.2 ± 24.9 | 60.8 ± 4.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | -59.7 ± 8.8 ✱ | -212.5 ± 27.5 ✱ | -59.8 ± 8.9 ✱ |
| look+roundrobin (lower bound) | 38.4 ± 1.2 | 85.8 ± 1.5 | 108.8 ± 4.2 | 65.1 ± 1.5 | 0.0 ± 0.0 | 100.0 ± 0.0 | -56.8 ± 11.4 ✱ | -239.3 ± 34.3 ✱ | -55.5 ± 11.3 ✱ |
| look+nearest | 20.9 ± 1.3 | 79.2 ± 4.5 | 127.5 ± 8.5 | 49.0 ± 1.6 | 0.0 ± 0.0 | 100.0 ± 0.0 | -74.3 ± 11.4 ✱ | -245.9 ± 32.7 ✱ | -71.6 ± 11.3 ✱ |
| look+cost | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | -81.4 ± 11.7 ✱ | -286.6 ± 33.5 ✱ | -79.8 ± 11.6 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 26.3 | 0.0 | 54.8 | 40.1 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 21.8 | 0.0 | 41.5 | 31.3 |
| fifo+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 7.6 | 0.0 | 19.5 | 7.9 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 8.1 | 0.0 | 22.4 | 7.3 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 5.0 | 0.0 | 12.1 | 3.2 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |

