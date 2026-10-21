# H1: chaos schedule vs no faults, same passengers (held-out seeds 21-40)

- **Command:** `elevator bench --scenarios chaos --strategies look+cost,look+nearest --faults on,off --baseline look+cost/no-faults --seeds 20 --first-seed 21 --out docs/results/h1-chaos.md --title "H1: chaos schedule vs no faults, same passengers (held-out seeds 21-40)"`
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** look+cost/no-faults
- **Scenarios:** chaos (4 elevators, 20 floors, capacity 10, next-trip)
- **Runs:** 80, 1.9 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## chaos

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 19.6 ± 1.1 | 58.9 ± 4.1 | 104.1 ± 8.3 | 50.0 ± 1.7 | 0.0 ± 0.0 | 100.0 ± 0.0 | 5.8 ± 0.8 ✱ | 20.5 ± 3.3 ✱ | 9.2 ± 1.1 ✱ |
| look+cost/no-faults (baseline) | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |
| look+nearest | 25.6 ± 1.6 | 87.0 ± 3.8 | 152.7 ± 13.7 | 57.5 ± 2.2 | 0.0 ± 0.0 | 100.0 ± 0.0 | 11.8 ± 1.3 ✱ | 48.5 ± 3.9 ✱ | 16.7 ± 1.6 ✱ |
| look+nearest/no-faults | 21.0 ± 1.3 | 78.1 ± 4.3 | 127.8 ± 8.4 | 49.1 ± 1.6 | 0.0 ± 0.0 | 100.0 ± 0.0 | 7.2 ± 0.8 ✱ | 39.6 ± 3.8 ✱ | 8.3 ± 0.9 ✱ |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 0.0 | 0.0 | 0.0 | 0.3 | 0.6 | 4.5 | 0.0 | 12.5 | 1.8 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |
| look+nearest | 0.0 | 0.0 | 0.1 | 0.5 | 0.8 | 6.8 | 0.0 | 13.9 | 3.5 |
| look+nearest/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | 0.7 | 5.1 | 0.0 | 12.1 | 3.1 |

Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):

| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 600.0 | 2.0 | 90.0 | 2.0 | 31.9 | 119.1 | 5.2 | 68.5 | 0.0 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 0.0 |
| look+nearest | 600.0 | 2.0 | 90.0 | 2.0 | 31.2 | 120.0 | 10.6 | 134.4 | 0.0 |
| look+nearest/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 0.0 |

