# Perf: real clock, real threads

- **Command:** `elevator perf --levels 100,1000,10000 --soak-seconds 0 --overload-threshold none --out docs/results/perf-cost-only.md`
- **System:** look+cost, 4 elevators, 20 floors, capacity 10
- **Machine:** 10 logical cores, .NET 10.0.9, macOS 26.5.0
- **Latency:** wall clock from `AssignRequest` to the accepted assignment (queueing in the bounded request channel included).
- **Overload fallback:** disabled

| Submitters | Requests | Assigned | Lost | Dup | p50 ms | p95 ms | p99 ms | max ms | req/s | decided by fallback |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 100 | 100 | 0 | 0 | 3.27 | 9.55 | 10.12 | 10.19 | 9671 | 0 of 103 |
| 1000 | 1000 | 1000 | 0 | 0 | 135.58 | 320.51 | 332.80 | 335.46 | 2973 | 0 of 1006 |
| 10000 | 10000 | 10000 | 0 | 0 | 580.81 | 877.36 | 903.58 | 909.31 | 10684 | 0 of 10009 |

Pipeline per request at 10000 submitters (wall clock):

| Actor | Messages / request | Handler µs / request | Delivery µs / request |
|---|---|---|---|
| Dispatcher | 3.00 | 88.71 | 4.39 |
| Elevator | 1.00 | 7.14 | 1.67 |
| Passengers | 2.00 | 0.61 | 0.92 |

| SLO | Measured | Result |
|---|---|---|
| Assignment p99 < 100 ms @ 100 concurrent | 10.12 ms | PASS |
| Assignment p99 < 100 ms @ 1000 concurrent | 332.80 ms | FAIL |
| Assignment p99 < 100 ms @ 10000 concurrent | 903.58 ms | FAIL |
| Handles 100+ concurrent requests (all assigned) | 100/100, 1000/1000, 10000/10000 | PASS |
| Lost assignments = 0 | 0 | PASS |
| Duplicated assignments = 0 | 0 | PASS |
