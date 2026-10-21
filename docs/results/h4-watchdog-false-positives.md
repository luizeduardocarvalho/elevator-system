# H4: watchdog false positives on fault-free runs vs T (held-out seeds 21-40)

- **Command:** `elevator bench --scenarios out/watchdog/up-peak-t5.yaml,out/watchdog/up-peak-t10.yaml,out/watchdog/up-peak-t30.yaml,out/watchdog/down-peak-t5.yaml,out/watchdog/down-peak-t10.yaml,out/watchdog/down-peak-t30.yaml,out/watchdog/fleet-t5.yaml,out/watchdog/fleet-t10.yaml,out/watchdog/fleet-t30.yaml --strategies fifo+nearest,look+nearest,look+cost --baseline look+cost --seeds 20 --first-seed 21 --out docs/results/h4-watchdog-false-positives.md --title "H4: watchdog false positives on fault-free runs vs T (held-out seeds 21-40)"`
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** look+cost
- **Scenarios:** up-peak-t5 (4 elevators, 20 floors, capacity 10, next-trip), up-peak-t10 (4 elevators, 20 floors, capacity 10, next-trip), up-peak-t30 (4 elevators, 20 floors, capacity 10, next-trip), down-peak-t5 (4 elevators, 20 floors, capacity 10, next-trip), down-peak-t10 (4 elevators, 20 floors, capacity 10, next-trip), down-peak-t30 (4 elevators, 20 floors, capacity 10, next-trip), fleet-t5 (4 elevators, 20 floors, capacity 10, next-trip), fleet-t10 (4 elevators, 20 floors, capacity 10, next-trip), fleet-t30 (4 elevators, 20 floors, capacity 10, next-trip)
- **Runs:** 540, 3.0 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## up-peak-t5

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 26.9 ± 1.8 | 103.9 ± 10.1 | 205.7 ± 22.3 | 60.9 ± 2.1 | 5.1 ± 2.1 | 100.0 ± 0.0 | 11.1 ± 1.6 ✱ | 66.1 ± 9.9 ✱ | 11.6 ± 1.6 ✱ |
| look+nearest | 20.7 ± 1.1 | 59.4 ± 9.4 | 146.7 ± 14.4 | 55.9 ± 1.2 | 4.8 ± 2.1 | 100.0 ± 0.0 | 5.0 ± 1.3 ✱ | 21.5 ± 9.8 ✱ | 6.6 ± 1.2 ✱ |
| look+cost (baseline) | 15.8 ± 0.6 | 37.9 ± 0.8 | 57.6 ± 6.4 | 49.3 ± 0.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.7 | 0.0 | 0.4 | 4.5 | 0.0 | 17.3 | 4.0 |
| look+nearest | 0.0 | 0.0 | 0.5 | 0.0 | 0.4 | 3.1 | 0.0 | 15.3 | 1.4 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.2 | 0.0 | 14.0 | 0.2 |

## up-peak-t10

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 26.9 ± 1.8 | 103.9 ± 10.1 | 205.7 ± 22.3 | 60.9 ± 2.1 | 5.1 ± 2.1 | 100.0 ± 0.0 | 11.1 ± 1.6 ✱ | 66.1 ± 9.9 ✱ | 11.6 ± 1.6 ✱ |
| look+nearest | 20.7 ± 1.1 | 59.4 ± 9.4 | 146.7 ± 14.4 | 55.9 ± 1.2 | 4.8 ± 2.1 | 100.0 ± 0.0 | 5.0 ± 1.3 ✱ | 21.5 ± 9.8 ✱ | 6.6 ± 1.2 ✱ |
| look+cost (baseline) | 15.8 ± 0.6 | 37.9 ± 0.8 | 57.6 ± 6.4 | 49.3 ± 0.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.7 | 0.0 | 0.4 | 4.5 | 0.0 | 17.3 | 4.0 |
| look+nearest | 0.0 | 0.0 | 0.5 | 0.0 | 0.4 | 3.1 | 0.0 | 15.3 | 1.4 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.2 | 0.0 | 14.0 | 0.2 |

## up-peak-t30

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 26.9 ± 1.8 | 103.9 ± 10.1 | 205.7 ± 22.3 | 60.9 ± 2.1 | 5.1 ± 2.1 | 100.0 ± 0.0 | 11.1 ± 1.6 ✱ | 66.1 ± 9.9 ✱ | 11.6 ± 1.6 ✱ |
| look+nearest | 20.7 ± 1.1 | 59.4 ± 9.4 | 146.7 ± 14.4 | 55.9 ± 1.2 | 4.8 ± 2.1 | 100.0 ± 0.0 | 5.0 ± 1.3 ✱ | 21.5 ± 9.8 ✱ | 6.6 ± 1.2 ✱ |
| look+cost (baseline) | 15.8 ± 0.6 | 37.9 ± 0.8 | 57.6 ± 6.4 | 49.3 ± 0.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.7 | 0.0 | 0.4 | 4.5 | 0.0 | 17.3 | 4.0 |
| look+nearest | 0.0 | 0.0 | 0.5 | 0.0 | 0.4 | 3.1 | 0.0 | 15.3 | 1.4 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.3 | 1.2 | 0.0 | 14.0 | 0.2 |

## down-peak-t5

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 251.1 ± 13.8 | 584.6 ± 33.1 | 718.6 ± 40.1 | 275.8 ± 13.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | 230.5 ± 13.5 ✱ | 531.5 ± 32.4 ✱ | 226.4 ± 13.4 ✱ |
| look+nearest | 29.3 ± 1.1 | 87.7 ± 3.7 | 169.2 ± 24.4 | 60.2 ± 1.4 | 0.1 ± 0.1 | 100.0 ± 0.0 | 8.7 ± 0.9 ✱ | 34.5 ± 3.8 ✱ | 10.8 ± 0.9 ✱ |
| look+cost (baseline) | 20.6 ± 0.7 | 53.2 ± 2.1 | 94.0 ± 7.4 | 49.4 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 52.0 | 0.0 | 105.4 | 93.0 |
| look+nearest | 0.0 | 0.0 | 0.4 | 0.0 | 0.8 | 6.6 | 0.0 | 16.7 | 4.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 3.6 | 0.0 | 14.2 | 2.1 |

## down-peak-t10

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 251.1 ± 13.8 | 584.6 ± 33.1 | 718.6 ± 40.1 | 275.8 ± 13.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | 230.5 ± 13.5 ✱ | 531.5 ± 32.4 ✱ | 226.4 ± 13.4 ✱ |
| look+nearest | 29.3 ± 1.1 | 87.7 ± 3.7 | 169.2 ± 24.4 | 60.2 ± 1.4 | 0.1 ± 0.1 | 100.0 ± 0.0 | 8.7 ± 0.9 ✱ | 34.5 ± 3.8 ✱ | 10.8 ± 0.9 ✱ |
| look+cost (baseline) | 20.6 ± 0.7 | 53.2 ± 2.1 | 94.0 ± 7.4 | 49.4 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 52.0 | 0.0 | 105.4 | 93.0 |
| look+nearest | 0.0 | 0.0 | 0.4 | 0.0 | 0.8 | 6.6 | 0.0 | 16.7 | 4.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 3.6 | 0.0 | 14.2 | 2.1 |

## down-peak-t30

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 251.1 ± 13.8 | 584.6 ± 33.1 | 718.6 ± 40.1 | 275.8 ± 13.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | 230.5 ± 13.5 ✱ | 531.5 ± 32.4 ✱ | 226.4 ± 13.4 ✱ |
| look+nearest | 29.3 ± 1.1 | 87.7 ± 3.7 | 169.2 ± 24.4 | 60.2 ± 1.4 | 0.1 ± 0.1 | 100.0 ± 0.0 | 8.7 ± 0.9 ✱ | 34.5 ± 3.8 ✱ | 10.8 ± 0.9 ✱ |
| look+cost (baseline) | 20.6 ± 0.7 | 53.2 ± 2.1 | 94.0 ± 7.4 | 49.4 ± 0.9 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 52.0 | 0.0 | 105.4 | 93.0 |
| look+nearest | 0.0 | 0.0 | 0.4 | 0.0 | 0.8 | 6.6 | 0.0 | 16.7 | 4.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 3.6 | 0.0 | 14.2 | 2.1 |

## fleet-t5

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 94.1 ± 12.0 | 370.2 ± 45.8 | 492.4 ± 58.1 | 118.0 ± 12.1 | 0.0 ± 0.0 | 93.4 ± 0.7 | 79.4 ± 11.3 ✱ | 325.6 ± 43.7 ✱ | 79.2 ± 11.3 ✱ |
| look+nearest | 21.3 ± 1.5 | 76.0 ± 5.0 | 128.3 ± 12.3 | 48.0 ± 2.0 | 0.0 ± 0.0 | 93.4 ± 0.7 | 6.7 ± 1.1 ✱ | 31.4 ± 4.6 ✱ | 9.2 ± 1.3 ✱ |
| look+cost (baseline) | 14.6 ± 0.8 | 44.6 ± 3.2 | 82.3 ± 7.3 | 38.8 ± 1.1 | 0.0 ± 0.0 | 93.4 ± 0.7 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.1 | 0.0 | 0.7 | 24.3 | 0.0 | 39.2 | 29.8 |
| look+nearest | 0.0 | 0.0 | 0.1 | 0.0 | 0.8 | 5.6 | 0.0 | 11.9 | 2.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 2.9 | 0.0 | 10.0 | 1.2 |

Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):

| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.0 | 61.3 | 5.0 | 1.0 | 43.8 | 2.6 | 16.9 |
| look+nearest | 0.0 | 0.0 | 0.0 | 62.3 | 5.0 | 1.0 | 35.3 | 2.6 | 16.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 64.6 | 5.0 | 1.0 | 19.1 | 2.6 | 16.9 |

## fleet-t10

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 95.4 ± 10.4 | 360.5 ± 42.3 | 526.9 ± 60.3 | 120.3 ± 10.5 | 0.0 ± 0.0 | 93.4 ± 0.7 | 81.5 ± 10.0 ✱ | 320.7 ± 40.3 ✱ | 81.8 ± 9.9 ✱ |
| look+nearest | 22.8 ± 1.3 | 81.9 ± 4.0 | 158.8 ± 12.8 | 51.3 ± 2.1 | 0.0 ± 0.0 | 93.4 ± 0.7 | 8.8 ± 1.0 ✱ | 42.1 ± 3.0 ✱ | 12.8 ± 1.5 ✱ |
| look+cost (baseline) | 14.0 ± 0.8 | 39.8 ± 3.2 | 80.8 ± 8.2 | 38.5 ± 1.0 | 0.0 ± 0.0 | 93.4 ± 0.7 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 25.7 | 0.0 | 39.5 | 29.5 |
| look+nearest | 0.0 | 0.0 | 0.1 | 0.0 | 0.9 | 6.5 | 0.0 | 12.2 | 3.1 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 2.7 | 0.0 | 9.6 | 1.1 |

Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):

| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 16.9 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 16.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 16.9 |

## fleet-t30

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 95.4 ± 10.4 | 360.5 ± 42.3 | 526.9 ± 60.3 | 120.3 ± 10.5 | 0.0 ± 0.0 | 93.4 ± 0.7 | 81.5 ± 10.0 ✱ | 320.7 ± 40.3 ✱ | 81.8 ± 9.9 ✱ |
| look+nearest | 22.8 ± 1.3 | 81.9 ± 4.0 | 158.8 ± 12.8 | 51.3 ± 2.1 | 0.0 ± 0.0 | 93.4 ± 0.7 | 8.8 ± 1.0 ✱ | 42.1 ± 3.0 ✱ | 12.8 ± 1.5 ✱ |
| look+cost (baseline) | 14.0 ± 0.8 | 39.8 ± 3.2 | 80.8 ± 8.2 | 38.5 ± 1.0 | 0.0 ± 0.0 | 93.4 ± 0.7 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 25.7 | 0.0 | 39.5 | 29.5 |
| look+nearest | 0.0 | 0.0 | 0.1 | 0.0 | 0.9 | 6.5 | 0.0 | 12.2 | 3.1 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 2.7 | 0.0 | 9.6 | 1.1 |

Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):

| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |
|---|---|---|---|---|---|---|---|---|---|
| fifo+nearest | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 16.9 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 16.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 16.9 |

