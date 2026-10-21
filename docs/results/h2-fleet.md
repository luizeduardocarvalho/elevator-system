# H2: dispatch on a typed fleet with access policies (held-out seeds 21-40)

- **Command:** `elevator bench --scenarios fleet --scheduling look --dispatch roundrobin,nearest,cost --baseline look+nearest --seeds 20 --first-seed 21 --out docs/results/h2-fleet.md --title "H2: dispatch on a typed fleet with access policies (held-out seeds 21-40)"`
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** look+nearest
- **Scenarios:** fleet (4 elevators, 20 floors, capacity 10, next-trip)
- **Runs:** 60, 0.6 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## fleet

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+roundrobin (lower bound) | 44.6 ± 1.9 | 117.4 ± 4.8 | 177.3 ± 11.6 | 73.9 ± 2.4 | 0.0 ± 0.0 | 93.4 ± 0.7 | 21.8 ± 1.8 ✱ | 35.4 ± 6.2 ✱ | 22.6 ± 2.1 ✱ |
| look+nearest (baseline) | 22.8 ± 1.3 | 81.9 ± 4.0 | 158.8 ± 12.8 | 51.3 ± 2.1 | 0.0 ± 0.0 | 93.4 ± 0.7 | – | – | – |
| look+cost | 14.0 ± 0.8 | 39.8 ± 3.2 | 80.8 ± 8.2 | 38.5 ± 1.0 | 0.0 ± 0.0 | 93.4 ± 0.7 | -8.8 ± 1.0 ✱ | -42.1 ± 3.0 ✱ | -12.8 ± 1.5 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+roundrobin | 0.0 | 0.0 | 0.1 | 0.0 | 0.9 | 13.6 | 0.0 | 22.3 | 7.7 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | 0.9 | 6.6 | 0.0 | 12.2 | 3.1 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 2.7 | 0.0 | 9.6 | 1.1 |

Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):

| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |
|---|---|---|---|---|---|---|---|---|---|
| look+roundrobin | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 16.9 |
| look+nearest | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 16.9 |
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 16.9 |

