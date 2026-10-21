# Perf: real clock, real threads

- **Command:** `elevator perf --dispatch nearest --overload-threshold none --levels 10000 --soak-seconds 0 --out docs/results/perf-nearest.md`
- **System:** look+nearest, 4 elevators, 20 floors, capacity 10
- **Machine:** 10 logical cores, .NET 10.0.9, macOS 26.5.0
- **Latency:** wall clock from `AssignRequest` to the accepted assignment (queueing in the bounded request channel included).
- **Overload fallback:** disabled

| Submitters | Requests | Assigned | Lost | Dup | p50 ms | p95 ms | p99 ms | max ms | req/s | decided by fallback |
|---|---|---|---|---|---|---|---|---|---|---|
| 10000 | 10000 | 10000 | 0 | 0 | 87.04 | 142.85 | 147.46 | 151.65 | 57315 | 0 of 10000 |

Pipeline per request at 10000 submitters (wall clock):

| Actor | Messages / request | Handler µs / request | Delivery µs / request |
|---|---|---|---|
| Dispatcher | 3.00 | 13.75 | 2.81 |
| Elevator | 1.00 | 7.01 | 4.71 |
| Passengers | 2.00 | 0.89 | 2.01 |

| SLO | Measured | Result |
|---|---|---|
| Assignment p99 < 100 ms @ 10000 concurrent | 147.46 ms | FAIL |
| Handles 100+ concurrent requests (all assigned) | 10000/10000 | PASS |
| Lost assignments = 0 | 0 | PASS |
| Duplicated assignments = 0 | 0 | PASS |
