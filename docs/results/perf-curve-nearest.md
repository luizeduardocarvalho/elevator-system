# Perf: real clock, real threads

- **Command:** `elevator perf --levels 100,300,1000,3000,5000,6500,8000,10000 --soak-seconds 0 --dispatch nearest --overload-threshold none --out docs/results/perf-curve-nearest.md`
- **System:** look+nearest, 4 elevators, 20 floors, capacity 10
- **Machine:** 10 logical cores, .NET 10.0.9, macOS 26.5.0
- **Latency:** wall clock from `AssignRequest` to the accepted assignment (queueing in the bounded request channel included).
- **Overload fallback:** disabled

| Submitters | Requests | Assigned | Lost | Dup | p50 ms | p95 ms | p99 ms | max ms | req/s | decided by fallback |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 100 | 100 | 0 | 0 | 0.17 | 0.44 | 0.57 | 0.67 | 79083 | 0 of 100 |
| 300 | 300 | 300 | 0 | 0 | 3.86 | 7.19 | 7.21 | 7.22 | 37746 | 0 of 300 |
| 1000 | 1000 | 1000 | 0 | 0 | 22.60 | 26.42 | 26.50 | 26.52 | 31699 | 0 of 1000 |
| 3000 | 3000 | 3000 | 0 | 0 | 31.21 | 40.93 | 44.60 | 46.69 | 55770 | 0 of 3000 |
| 5000 | 5000 | 5000 | 0 | 0 | 89.04 | 171.11 | 172.24 | 204.80 | 24054 | 0 of 5000 |
| 6500 | 6500 | 6500 | 0 | 0 | 72.81 | 149.09 | 154.42 | 155.14 | 33968 | 0 of 6500 |
| 8000 | 8000 | 8000 | 0 | 0 | 101.63 | 125.34 | 127.18 | 128.10 | 47731 | 0 of 8000 |
| 10000 | 10000 | 10000 | 0 | 0 | 120.93 | 173.77 | 177.36 | 188.11 | 41654 | 0 of 10000 |

Pipeline per request at 10000 submitters (wall clock):

| Actor | Messages / request | Handler µs / request | Delivery µs / request |
|---|---|---|---|
| Dispatcher | 3.00 | 19.76 | 3.41 |
| Elevator | 1.00 | 11.07 | 4.54 |
| Passengers | 2.00 | 0.98 | 1.25 |

| SLO | Measured | Result |
|---|---|---|
| Assignment p99 < 100 ms @ 100 concurrent | 0.57 ms | PASS |
| Assignment p99 < 100 ms @ 300 concurrent | 7.21 ms | PASS |
| Assignment p99 < 100 ms @ 1000 concurrent | 26.50 ms | PASS |
| Assignment p99 < 100 ms @ 3000 concurrent | 44.60 ms | PASS |
| Assignment p99 < 100 ms @ 5000 concurrent | 172.24 ms | FAIL |
| Assignment p99 < 100 ms @ 6500 concurrent | 154.42 ms | FAIL |
| Assignment p99 < 100 ms @ 8000 concurrent | 127.18 ms | FAIL |
| Assignment p99 < 100 ms @ 10000 concurrent | 177.36 ms | FAIL |
| Handles 100+ concurrent requests (all assigned) | 100/100, 300/300, 1000/1000, 3000/3000, 5000/5000, 6500/6500, 8000/8000, 10000/10000 | PASS |
| Lost assignments = 0 | 0 | PASS |
| Duplicated assignments = 0 | 0 | PASS |
