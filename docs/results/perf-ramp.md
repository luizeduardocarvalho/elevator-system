# Perf: open-loop steady-state throughput ramp

- **Command:** `elevator perf --ramp --out docs/results/perf-ramp.md`
- **Machine:** 10 logical cores, .NET 10.0.9, macOS 26.5.0; load average at start: 2.80 4.34 5.15
- **System:** look+cost, 4 elevators, 20 floors, capacity 10, overload fallback to Nearest above 200 in flight
- **Seed (steady state):** lunch at its busiest moment, 671.000s: 15 outstanding (E0 2 riding + 1 to pick up, E1 2 riding + 1 to pick up, E2 0 riding + 4 to pick up, E3 3 riding + 2 to pick up). Building frozen at that moment; every request retired on acceptance, so the backlog stays constant.
- **Load:** open loop, one generator thread; request i due at t0 + i/rate. 5 s warm-up discarded, 30 s measured per step; 3 repeats, each rate reports its median-p99 repeat. The ramp stops after the first step whose achieved rate is below 99% of offered.
- **Latency:** wall clock from the *intended* send time to the accepted assignment (no coordinated omission). A step is **invalid** if the generator's own lag p99 exceeds 1 ms.
- **Little's law:** in-flight sampled every ~1 ms vs λ·W (achieved × mean time in system); Δ > 5 % is flagged.

| Offered /s | Achieved /s | p50 ms | p99 ms | p99.9 ms | max ms | In flight (sampled) | λ·W | Little Δ | By fallback | GC 0/1/2 | Lag p99 / max ms | Valid | Repeats |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1000 | 1000 | 0.04 | 0.21 | 1.85 | 5.47 | 0.0 | 0.0 | 7.0 % ⚠ | 0.0 % | 142/4/1 | 0.002 / 3.3 | yes | 3 |
| 2000 | 2000 | 0.04 | 23.73 | 67.64 | 88.16 | 1.0 | 1.3 | 22.0 % ⚠ | 0.0 % | 279/20/0 | 15.035 / 87.0 | **no** | 3 |
| 5000 | 5000 | 0.04 | 0.38 | 7.53 | 26.21 | 0.3 | 0.3 | 9.2 % ⚠ | 0.0 % | 706/3/0 | 0.057 / 4.1 | yes | 3 |
| 10000 | 10000 | 0.03 | 0.90 | 7.04 | 20.17 | 0.6 | 0.7 | 3.1 % | 0.0 % | 1414/3/0 | 0.407 / 9.3 | yes | 3 |
| 15000 | 15000 | 0.03 | 1.03 | 18.40 | 31.02 | 1.3 | 1.2 | 11.8 % ⚠ | 0.0 % | 2120/4/1 | 0.711 / 12.4 | yes | 3 |
| 20000 | 20000 | 0.03 | 1.01 | 9.43 | 18.45 | 1.7 | 1.6 | 8.7 % ⚠ | 0.0 % | 2811/160/0 | 0.464 / 9.1 | yes | 3 |
| 30000 | 30001 | 0.04 | 2.13 | 13.21 | 17.56 | 3.4 | 3.9 | 11.3 % ⚠ | 0.0 % | 4064/4/1 | 0.560 / 6.4 | yes | 3 |
| 40000 | 40000 | 0.07 | 7.74 | 25.44 | 35.89 | 12.4 | 14.0 | 11.6 % ⚠ | 0.2 % | 5443/9/2 | 0.987 / 16.0 | yes | 3 |
| 50000 | 50002 | 0.91 | 27.84 | 48.14 | 59.00 | 475.0 | 511.7 | 7.2 % ⚠ | 5.8 % | 6898/831/413 | 1.349 / 22.7 | **no** | 3 |
| 60000 | 60003 | 20.46 | 46.09 | 95.20 | 108.80 | 1254.3 | 1253.8 | 0.0 % | 48.3 % | 5443/1097/423 | 5.024 / 29.7 | **no** | 3 |
| 70000 | 70013 | 18.23 | 27.80 | 38.29 | 44.69 | 1319.3 | 1310.2 | 0.7 % | 66.0 % | 5041/1104/431 | 2.097 / 9.7 | **no** | 3 |
