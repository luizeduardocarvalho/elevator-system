# M5: full grid under next-trip capacity (held-out seeds 21-40)

- **Command:** `elevator bench --scheduling fifo,look --dispatch roundrobin,nearest,cost --seeds 20 --first-seed 21 --out docs/results/m5-next-trip.md --title "M5: full grid under next-trip capacity (held-out seeds 21-40)"`
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** fifo+nearest
- **Scenarios:** up-peak (4 elevators, 20 floors, capacity 10, next-trip), down-peak (4 elevators, 20 floors, capacity 10, next-trip), lunch (4 elevators, 20 floors, capacity 10, next-trip)
- **Runs:** 360, 1.9 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 49.7 ± 2.4 | 122.4 ± 9.5 | 222.6 ± 24.3 | 79.7 ± 2.8 | 0.1 ± 0.2 | 100.0 ± 0.0 | 22.8 ± 2.6 ✱ | 18.5 ± 12.7 ✱ | 18.8 ± 2.9 ✱ |
| fifo+nearest (baseline) | 26.9 ± 1.8 | 103.9 ± 10.1 | 205.7 ± 22.3 | 60.9 ± 2.1 | 5.1 ± 2.1 | 100.0 ± 0.0 | – | – | – |
| fifo+cost | 17.0 ± 0.6 | 40.1 ± 1.6 | 86.5 ± 16.2 | 49.7 ± 1.0 | 0.0 ± 0.0 | 100.0 ± 0.0 | -9.9 ± 1.6 ✱ | -63.9 ± 10.0 ✱ | -11.2 ± 1.7 ✱ |
| look+roundrobin (lower bound) | 39.0 ± 1.0 | 86.5 ± 2.0 | 116.7 ± 10.7 | 69.3 ± 1.5 | 0.0 ± 0.0 | 100.0 ± 0.0 | 12.1 ± 1.6 ✱ | -17.5 ± 8.9 ✱ | 8.4 ± 1.7 ✱ |
| look+nearest | 20.7 ± 1.1 | 59.4 ± 9.4 | 146.7 ± 14.4 | 55.9 ± 1.2 | 4.8 ± 2.1 | 100.0 ± 0.0 | -6.1 ± 2.1 ✱ | -44.5 ± 13.1 ✱ | -5.0 ± 2.1 ✱ |
| look+cost | 15.8 ± 0.6 | 37.9 ± 0.8 | 57.6 ± 6.4 | 49.3 ± 0.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | -11.1 ± 1.6 ✱ | -66.1 ± 9.9 ✱ | -11.6 ± 1.6 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 9.6 | 0.0 | 28.0 | 11.4 |
| fifo+nearest | 0.0 | 0.0 | 0.7 | 0.0 | 0.4 | 4.5 | 0.0 | 17.3 | 4.0 |
| fifo+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.4 | 0.0 | 14.5 | 0.7 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 7.1 | 0.0 | 23.8 | 7.5 |
| look+nearest | 0.0 | 0.0 | 0.5 | 0.0 | 0.4 | 3.1 | 0.0 | 15.3 | 1.4 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.2 | 0.0 | 14.0 | 0.2 |

## down-peak

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 273.9 ± 13.2 | 589.6 ± 30.1 | 683.2 ± 36.7 | 298.5 ± 13.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | 22.8 ± 9.3 ✱ | 4.9 ± 28.1 | 22.7 ± 9.3 ✱ |
| fifo+nearest (baseline) | 251.1 ± 13.8 | 584.6 ± 33.1 | 718.6 ± 40.1 | 275.8 ± 13.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| fifo+cost | 78.6 ± 3.3 | 169.0 ± 7.2 | 233.0 ± 17.5 | 103.5 ± 3.4 | 0.0 ± 0.0 | 100.0 ± 0.0 | -172.4 ± 11.9 ✱ | -415.6 ± 29.2 ✱ | -172.3 ± 11.9 ✱ |
| look+roundrobin (lower bound) | 40.6 ± 1.3 | 88.1 ± 2.2 | 117.3 ± 7.7 | 70.9 ± 1.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | -210.5 ± 13.2 ✱ | -496.5 ± 31.9 ✱ | -204.9 ± 12.9 ✱ |
| look+nearest | 29.3 ± 1.1 | 87.7 ± 3.7 | 169.2 ± 24.4 | 60.2 ± 1.4 | 0.1 ± 0.1 | 100.0 ± 0.0 | -221.8 ± 13.2 ✱ | -497.0 ± 31.2 ✱ | -215.6 ± 13.1 ✱ |
| look+cost | 20.6 ± 0.7 | 53.2 ± 2.1 | 94.0 ± 7.4 | 49.4 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | -230.5 ± 13.5 ✱ | -531.5 ± 32.4 ✱ | -226.4 ± 13.4 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 55.3 | 0.0 | 116.3 | 101.7 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 52.0 | 0.0 | 105.4 | 93.0 |
| fifo+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 15.8 | 0.0 | 38.3 | 24.1 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 8.7 | 0.0 | 22.9 | 8.3 |
| look+nearest | 0.0 | 0.0 | 0.4 | 0.0 | 0.8 | 6.6 | 0.0 | 16.7 | 4.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 3.6 | 0.0 | 14.2 | 2.1 |

## lunch

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin (lower bound) | 121.1 ± 11.3 | 356.0 ± 34.2 | 476.4 ± 37.1 | 145.8 ± 11.4 | 0.0 ± 0.0 | 100.0 ± 0.0 | 25.7 ± 7.2 ✱ | 30.3 ± 27.6 ✱ | 25.1 ± 7.2 ✱ |
| fifo+nearest (baseline) | 95.4 ± 12.2 | 325.7 ± 35.2 | 455.6 ± 47.3 | 120.8 ± 12.4 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| fifo+cost | 35.5 ± 4.2 | 112.5 ± 13.7 | 197.2 ± 24.9 | 60.8 ± 4.3 | 0.0 ± 0.0 | 100.0 ± 0.0 | -59.8 ± 8.9 ✱ | -213.2 ± 27.7 ✱ | -60.0 ± 8.9 ✱ |
| look+roundrobin (lower bound) | 38.4 ± 1.2 | 85.8 ± 1.5 | 108.8 ± 4.2 | 65.1 ± 1.5 | 0.0 ± 0.0 | 100.0 ± 0.0 | -57.0 ± 11.4 ✱ | -239.9 ± 34.4 ✱ | -55.7 ± 11.3 ✱ |
| look+nearest | 21.0 ± 1.3 | 78.1 ± 4.3 | 127.8 ± 8.4 | 49.1 ± 1.6 | 0.0 ± 0.0 | 100.0 ± 0.0 | -74.4 ± 11.4 ✱ | -247.6 ± 32.9 ✱ | -71.7 ± 11.3 ✱ |
| look+cost | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | -81.6 ± 11.8 ✱ | -287.3 ± 33.6 ✱ | -80.0 ± 11.6 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 26.2 | 0.0 | 54.5 | 39.9 |
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 21.9 | 0.0 | 41.5 | 31.3 |
| fifo+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 7.6 | 0.0 | 19.5 | 7.9 |
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 8.1 | 0.0 | 22.4 | 7.3 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 5.1 | 0.0 | 12.1 | 3.1 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |

