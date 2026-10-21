# Perf: real clock, real threads

- **Command:** `elevator perf --dispatch nearest --overload-threshold none --levels 100,1000,10000 --soak-seconds 0 --out docs/results/perf-nearest.md`
- **System:** look+nearest, 4 elevators, 20 floors, capacity 10
- **Machine:** 10 logical cores, .NET 10.0.9, macOS 26.5.0
- **Latency:** wall clock from `AssignRequest` to the accepted assignment (queueing in the bounded request channel included).
- **Overload fallback:** disabled

| Submitters | Requests | Assigned | Lost | Dup | p50 ms | p95 ms | p99 ms | max ms | req/s | decided by fallback |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 100 | 100 | 0 | 0 | 0.58 | 0.70 | 0.70 | 0.73 | 107285 | 0 of 100 |
| 1000 | 1000 | 1000 | 0 | 0 | 5.41 | 15.16 | 15.44 | 15.74 | 52055 | 0 of 1000 |
| 10000 | 10000 | 10000 | 0 | 0 | 69.53 | 114.79 | 117.15 | 126.57 | 66298 | 0 of 10000 |

Pipeline per request at 10000 submitters (wall clock):

| Actor | Messages / request | Handler µs / request | Delivery µs / request |
|---|---|---|---|
| Dispatcher | 3.00 | 11.03 | 2.66 |
| Elevator | 1.00 | 6.15 | 5.37 |
| Passengers | 2.00 | 0.78 | 1.76 |

| SLO | Measured | Result |
|---|---|---|
| Assignment p99 < 100 ms @ 100 concurrent | 0.70 ms | PASS |
| Assignment p99 < 100 ms @ 1000 concurrent | 15.44 ms | PASS |
| Assignment p99 < 100 ms @ 10000 concurrent | 117.15 ms | FAIL |
| Handles 100+ concurrent requests (all assigned) | 100/100, 1000/1000, 10000/10000 | PASS |
| Lost assignments = 0 | 0 | PASS |
| Duplicated assignments = 0 | 0 | PASS |
