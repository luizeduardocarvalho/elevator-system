# H3: watchdog timeout T on the chaos schedule (held-out seeds 21-40)

- **Command:** `elevator bench --scenarios out/watchdog/chaos-t5.yaml,out/watchdog/chaos-t10.yaml,out/watchdog/chaos-t30.yaml,out/watchdog/chaos-t60.yaml --strategies look+cost --faults on,off --baseline look+cost/no-faults --seeds 20 --first-seed 21 --out docs/results/h3-watchdog-timeout.md --title "H3: watchdog timeout T on the chaos schedule (held-out seeds 21-40)"`
- **Seeds:** 21..40 (20 per strategy)
- **Baseline:** look+cost/no-faults
- **Scenarios:** chaos-t5 (4 elevators, 20 floors, capacity 10, next-trip), chaos-t10 (4 elevators, 20 floors, capacity 10, next-trip), chaos-t30 (4 elevators, 20 floors, capacity 10, next-trip), chaos-t60 (4 elevators, 20 floors, capacity 10, next-trip)
- **Runs:** 160, 1.4 s wall

All times are sim seconds. Each cell is the mean over seeds ± the 95% CI half-width (Student t).
Δ columns are **paired per-seed differences** against the baseline (common random numbers: every
strategy sees the same passengers for a given seed); negative is better. A Δ whose interval excludes 0 is marked ✱.

## chaos-t5

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 19.6 ± 1.1 | 57.4 ± 4.3 | 102.8 ± 6.0 | 49.9 ± 1.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | 5.8 ± 0.7 ✱ | 19.0 ± 3.4 ✱ | 9.1 ± 1.1 ✱ |
| look+cost/no-faults (baseline) | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 0.0 | 0.0 | 0.0 | 0.0 | 0.6 | 4.5 | 0.0 | 12.6 | 1.8 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |

Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):

| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 600.0 | 2.0 | 90.0 | 2.0 | 6.0 | 145.0 | 5.2 | 9.5 | 0.0 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 0.0 |

## chaos-t10

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 19.7 ± 1.2 | 57.4 ± 4.0 | 102.5 ± 6.2 | 50.0 ± 1.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | 5.9 ± 0.8 ✱ | 19.0 ± 3.4 ✱ | 9.2 ± 1.2 ✱ |
| look+cost/no-faults (baseline) | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 0.0 | 0.0 | 0.0 | 0.1 | 0.6 | 4.5 | 0.0 | 12.6 | 1.8 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |

Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):

| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 600.0 | 2.0 | 90.0 | 2.0 | 11.3 | 139.7 | 5.4 | 21.0 | 0.0 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 0.0 |

## chaos-t30

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 19.6 ± 1.1 | 58.9 ± 4.1 | 104.1 ± 8.3 | 50.0 ± 1.7 | 0.0 ± 0.0 | 100.0 ± 0.0 | 5.8 ± 0.8 ✱ | 20.5 ± 3.3 ✱ | 9.2 ± 1.1 ✱ |
| look+cost/no-faults (baseline) | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 0.0 | 0.0 | 0.0 | 0.3 | 0.6 | 4.5 | 0.0 | 12.5 | 1.8 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |

Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):

| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 600.0 | 2.0 | 90.0 | 2.0 | 31.9 | 119.1 | 5.2 | 68.5 | 0.0 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 0.0 |

## chaos-t60

| Strategy | Wait avg | Wait p95 | Wait max | Journey avg | Left behind | Served % | Δ wait avg | Δ wait p95 | Δ journey |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 19.8 ± 1.2 | 59.7 ± 4.4 | 123.7 ± 15.6 | 50.2 ± 1.8 | 0.0 ± 0.0 | 100.0 ± 0.0 | 6.0 ± 0.8 ✱ | 21.2 ± 3.4 ✱ | 9.4 ± 1.1 ✱ |
| look+cost/no-faults (baseline) | 13.8 ± 0.9 | 38.4 ± 2.7 | 79.7 ± 12.0 | 40.8 ± 1.1 | 0.0 ± 0.0 | 100.0 ± 0.0 | – | – | – |

Where the average wait goes (mean seconds per passenger; sums to the mean wait):

| Strategy | dispatch | reassignment | bypassed_full | blocked | finishing_door_cycle | serving_others | idle_start | travel_toward | travel_away |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 0.0 | 0.0 | 0.0 | 0.5 | 0.6 | 4.4 | 0.0 | 12.4 | 1.8 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | 0.5 | 2.5 | 0.0 | 10.0 | 0.9 |

Operations (mean per run; penalty = reassignment + blocked wait seconds, summed over passengers):

| Strategy | Maintenance s | Emergency stops | Emergency s | Faults | Detect s | Recover s | Pickups handed back | Penalty s | Refused |
|---|---|---|---|---|---|---|---|---|---|
| look+cost | 600.0 | 2.0 | 90.0 | 2.0 | 61.2 | 89.9 | 5.1 | 129.0 | 0.0 |
| look+cost/no-faults | 0.0 | 0.0 | 0.0 | 0.0 | – | – | 0.0 | 0.0 | 0.0 |

