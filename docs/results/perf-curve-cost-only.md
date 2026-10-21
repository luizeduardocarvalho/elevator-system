# Perf: real clock, real threads

- **Command:** `elevator perf --levels 100,300,1000,3000,5000,6500,8000,10000 --soak-seconds 0 --overload-threshold none --out docs/results/perf-curve-cost-only.md`
- **System:** look+cost, 4 elevators, 20 floors, capacity 10
- **Machine:** 10 logical cores, .NET 10.0.9, macOS 26.5.0
- **Latency:** wall clock from `AssignRequest` to the accepted assignment (queueing in the bounded request channel included).
- **Overload fallback:** disabled

| Submitters | Requests | Assigned | Lost | Dup | p50 ms | p95 ms | p99 ms | max ms | req/s | decided by fallback |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 100 | 100 | 0 | 0 | 10.52 | 20.93 | 21.04 | 21.24 | 4611 | 0 of 235 |
| 300 | 300 | 300 | 0 | 0 | 31.56 | 72.14 | 78.75 | 79.00 | 3729 | 0 of 330 |
| 1000 | 1000 | 1000 | 0 | 0 | 198.96 | 307.00 | 314.78 | 314.78 | 3146 | 0 of 1026 |
| 3000 | 3000 | 3000 | 0 | 0 | 258.46 | 512.82 | 532.07 | 532.48 | 5502 | 0 of 3030 |
| 5000 | 5000 | 5000 | 0 | 0 | 342.84 | 609.48 | 642.25 | 643.48 | 7308 | 0 of 5027 |
| 6500 | 6500 | 6500 | 0 | 0 | 381.54 | 766.77 | 814.69 | 825.75 | 7447 | 0 of 6581 |
| 8000 | 8000 | 8000 | 0 | 0 | 522.24 | 968.29 | 1013.35 | 1019.08 | 7605 | 0 of 9177 |
| 10000 | 10000 | 10000 | 0 | 0 | 582.04 | 1338.57 | 1422.13 | 1428.68 | 6636 | 0 of 12227 |

Pipeline per request at 10000 submitters (wall clock):

| Actor | Messages / request | Handler µs / request | Delivery µs / request |
|---|---|---|---|
| Dispatcher | 3.29 | 146.23 | 3.33 |
| Elevator | 1.22 | 14.25 | 2.04 |
| Passengers | 2.03 | 1.15 | 0.24 |

| SLO | Measured | Result |
|---|---|---|
| Assignment p99 < 100 ms @ 100 concurrent | 21.04 ms | PASS |
| Assignment p99 < 100 ms @ 300 concurrent | 78.75 ms | PASS |
| Assignment p99 < 100 ms @ 1000 concurrent | 314.78 ms | FAIL |
| Assignment p99 < 100 ms @ 3000 concurrent | 532.07 ms | FAIL |
| Assignment p99 < 100 ms @ 5000 concurrent | 642.25 ms | FAIL |
| Assignment p99 < 100 ms @ 6500 concurrent | 814.69 ms | FAIL |
| Assignment p99 < 100 ms @ 8000 concurrent | 1013.35 ms | FAIL |
| Assignment p99 < 100 ms @ 10000 concurrent | 1422.13 ms | FAIL |
| Handles 100+ concurrent requests (all assigned) | 100/100, 300/300, 1000/1000, 3000/3000, 5000/5000, 6500/6500, 8000/8000, 10000/10000 | PASS |
| Lost assignments = 0 | 0 | PASS |
| Duplicated assignments = 0 | 0 | PASS |
