# Perf: real clock, real threads

- **Command:** `elevator perf --levels 100,1000,10000 --soak-seconds 60 --out docs/results/perf.md`
- **System:** look+cost, 4 elevators, 20 floors, capacity 10
- **Machine:** 10 logical cores, .NET 10.0.9, macOS 26.5.0
- **Latency:** wall clock from `AssignRequest` to the accepted assignment (queueing in the bounded request channel included).
- **Overload fallback:** Nearest above 200 requests in flight

| Submitters | Requests | Assigned | Lost | Dup | p50 ms | p95 ms | p99 ms | max ms | req/s | decided by fallback |
|---|---|---|---|---|---|---|---|---|---|---|
| 100 | 100 | 100 | 0 | 0 | 5.33 | 11.51 | 12.18 | 12.23 | 8061 | 0 of 100 |
| 1000 | 1000 | 1000 | 0 | 0 | 48.36 | 53.15 | 53.61 | 53.71 | 18172 | 800 of 1000 |
| 10000 | 10000 | 10000 | 0 | 0 | 128.41 | 197.32 | 201.93 | 203.16 | 46140 | 9800 of 10000 |

Pipeline per request at 10000 submitters (wall clock):

| Actor | Messages / request | Handler µs / request | Delivery µs / request |
|---|---|---|---|
| Dispatcher | 3.00 | 17.83 | 3.14 |
| Elevator | 1.00 | 10.45 | 5.59 |
| Passengers | 2.00 | 1.13 | 1.30 |

Soak: 1190 submitted, 1183 delivered, heap samples (MB): 1.06, 1.09, 1.10, 1.12, 1.11, 1.08, 1.11, 1.12, 1.08, 1.12, 1.11, 1.12, 1.13, 1.12, 1.09, 1.13, 1.12, 1.11, 1.10, 1.12, 1.13, 1.13, 1.14, 1.14, 1.19, 1.13, 1.13, 1.11, 1.11, 1.14, 1.12, 1.15, 1.16, 1.12, 1.14, 1.13, 1.13, 1.16, 1.13, 1.17, 1.14, 1.13, 1.09, 1.11, 1.08, 1.11, 1.09, 1.07, 1.10, 1.10; p99 2.57 ms.

| SLO | Measured | Result |
|---|---|---|
| Assignment p99 < 100 ms @ 100 concurrent | 12.18 ms | PASS |
| Assignment p99 < 100 ms @ 1000 concurrent | 53.61 ms | PASS |
| Assignment p99 < 100 ms @ 10000 concurrent | 201.93 ms | FAIL |
| Handles 100+ concurrent requests (all assigned) | 100/100, 1000/1000, 10000/10000 | PASS |
| Lost assignments = 0 | 0 | PASS |
| Duplicated assignments = 0 | 0 | PASS |
| Memory: heap growth < 10% over 60 s soak | 0.87% (1.06 → 1.10 MB, 1183/1190 delivered) | PASS |
