# Perf: real clock, real threads

- **Command:** `elevator perf --levels 100,300,1000,3000,5000,6500,8000,10000 --soak-seconds 0 --out docs/results/perf-curve-default.md`
- **System:** look+cost, 4 elevators, 20 floors, capacity 10
- **Machine:** 10 logical cores, .NET 10.0.9, macOS 26.5.0
- **Latency:** wall clock from `AssignRequest` to the accepted assignment (queueing in the bounded request channel included).
- **Overload fallback:** Nearest above 200 requests in flight

| Submitters | Requests | Assigned | Lost | Dup | p50 ms | p95 ms | p99 ms | max ms | req/s | decided by fallback |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 100 | 100 | 0 | 0 | 20.97 | 27.32 | 27.42 | 27.57 | 3608 | 0 of 360 |
| 300 | 300 | 300 | 0 | 0 | 35.35 | 60.93 | 60.93 | 60.93 | 4876 | 100 of 400 |
| 1000 | 1000 | 1000 | 0 | 0 | 75.67 | 80.38 | 81.05 | 81.51 | 11999 | 804 of 1062 |
| 3000 | 3000 | 3000 | 0 | 0 | 79.67 | 99.58 | 100.10 | 109.06 | 26260 | 2800 of 3080 |
| 5000 | 5000 | 5000 | 0 | 0 | 135.27 | 181.66 | 185.24 | 185.34 | 24709 | 4800 of 5132 |
| 6500 | 6500 | 6500 | 0 | 0 | 146.74 | 203.88 | 207.16 | 208.90 | 26761 | 6300 of 6512 |
| 8000 | 8000 | 8000 | 0 | 0 | 140.60 | 198.45 | 204.19 | 207.77 | 30347 | 7800 of 8041 |
| 10000 | 10000 | 10000 | 0 | 0 | 186.88 | 295.94 | 319.90 | 323.58 | 26676 | 9800 of 10024 |

Pipeline per request at 10000 submitters (wall clock):

| Actor | Messages / request | Handler µs / request | Delivery µs / request |
|---|---|---|---|
| Dispatcher | 3.00 | 34.29 | 2.19 |
| Elevator | 1.00 | 8.21 | 4.52 |
| Passengers | 2.00 | 1.40 | 1.71 |

| SLO | Measured | Result |
|---|---|---|
| Assignment p99 < 100 ms @ 100 concurrent | 27.42 ms | PASS |
| Assignment p99 < 100 ms @ 300 concurrent | 60.93 ms | PASS |
| Assignment p99 < 100 ms @ 1000 concurrent | 81.05 ms | PASS |
| Assignment p99 < 100 ms @ 3000 concurrent | 100.10 ms | FAIL |
| Assignment p99 < 100 ms @ 5000 concurrent | 185.24 ms | FAIL |
| Assignment p99 < 100 ms @ 6500 concurrent | 207.16 ms | FAIL |
| Assignment p99 < 100 ms @ 8000 concurrent | 204.19 ms | FAIL |
| Assignment p99 < 100 ms @ 10000 concurrent | 319.90 ms | FAIL |
| Handles 100+ concurrent requests (all assigned) | 100/100, 300/300, 1000/1000, 3000/3000, 5000/5000, 6500/6500, 8000/8000, 10000/10000 | PASS |
| Lost assignments = 0 | 0 | PASS |
| Duplicated assignments = 0 | 0 | PASS |
