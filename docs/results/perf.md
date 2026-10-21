# Perf: real clock, real threads

- **Command:** `elevator perf --levels 100,1000,10000 --soak-seconds 60 --out docs/results/perf.md`
- **System:** look+cost, 4 elevators, 20 floors, capacity 10
- **Machine:** 10 logical cores, .NET 10.0.9, macOS 26.5.0
- **Latency:** wall clock from `AssignRequest` to the accepted assignment (queueing in the bounded request channel included).
- **Overload fallback:** Nearest above 200 requests in flight

| Submitters | Requests | Assigned | Lost | Dup | p50 ms | p95 ms | p99 ms | max ms | req/s | decided by fallback |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 100 | 100 | 0 | 0 | 3.57 | 10.77 | 11.39 | 11.42 | 8620 | 0 of 109 |
| 1000 | 1000 | 1000 | 0 | 0 | 43.19 | 48.10 | 49.69 | 49.79 | 19692 | 800 of 1006 |
| 10000 | 10000 | 10000 | 0 | 0 | 115.71 | 179.00 | 186.16 | 187.49 | 48729 | 9800 of 10008 |

Pipeline per request at 10000 submitters (wall clock):

| Actor | Messages / request | Handler µs / request | Delivery µs / request |
|---|---|---|---|
| Dispatcher | 3.00 | 15.60 | 4.09 |
| Elevator | 1.00 | 9.87 | 4.87 |
| Passengers | 2.00 | 0.72 | 1.15 |

Soak: 1173 submitted, 1165 delivered, heap samples (MB): 1.04, 1.08, 1.04, 1.07, 1.09, 1.05, 1.05, 1.06, 1.07, 1.09, 1.08, 1.07, 1.08, 1.06, 1.06, 1.06, 1.09, 1.12, 1.12, 1.11, 1.13, 1.08, 1.08, 1.10, 1.10, 1.08, 1.10, 1.09, 1.11, 1.11, 1.13, 1.10, 1.10, 1.10, 1.12, 1.07, 1.09, 1.12, 1.08, 1.07, 1.08, 1.08, 1.08, 1.08, 1.08, 1.11, 1.08, 1.09, 1.08, 1.09; p99 2.35 ms.

| SLO | Measured | Result |
|---|---|---|
| Assignment p99 < 100 ms @ 100 concurrent | 11.39 ms | PASS |
| Assignment p99 < 100 ms @ 1000 concurrent | 49.69 ms | PASS |
| Assignment p99 < 100 ms @ 10000 concurrent | 186.16 ms | FAIL |
| Handles 100+ concurrent requests (all assigned) | 100/100, 1000/1000, 10000/10000 | PASS |
| Lost assignments = 0 | 0 | PASS |
| Duplicated assignments = 0 | 0 | PASS |
| Memory: heap growth < 10% over 60 s soak | 1.71% (1.04 → 1.09 MB, 1165/1173 delivered) | PASS |
