# Perf ramp: predictions, written before the first full run

Committed before `elevator perf --ramp` was run on the full rate list, so they can be checked against the measurement
([perf-ramp](perf-ramp.md)) rather than fitted to it.

## Inputs (all measured earlier)

- **Seed.** Lunch at its busiest moment: 15 outstanding. Per elevator: E0 2 riding + 1 to pick up, E1 2 + 1,
  E2 0 + 4, E3 3 + 2. About 2 pickups per elevator.
- **Dispatcher protocol cost** ([perf-nearest](perf-nearest.md), Nearest, 10k burst).
  - 16.6 µs per request over 3 messages (submit, accept, snapshot). Of that, about 1.4 µs is Nearest's decision.
  - That leaves (16.6 − 1.4) / 3 ≈ **5.1 µs per dispatcher message**.
- **Cost decision** ([dispatch-decision-after](benchmarks/dispatch-decision-after.md)): 2.1 µs with no backlog and
  33 µs at 25 pending per elevator. Interpolated at about 2 per elevator, that's **≈ 5 µs** (range 3–8).

## What a ramp request costs the dispatcher

Retiring on accept adds two messages to the burst's three:
- the `RequestPickedUp` that retires it;
- the snapshot the elevator publishes after dropping the pickup.

That makes **5 dispatcher messages per request**. The dispatcher is the single serial stage, so a request's cost there
sets the ceiling.

| Path | Per request | Ceiling (1 / cost) |
|---|---|---|
| Nearest (the overload fallback) | 5 × 5.1 + 1.4 ≈ **26.9 µs** | **≈ 37k req/s** (range 32–42k) |
| Cost | 5 × 5.1 + 5 ≈ **30.5 µs** | **≈ 33k req/s** (range 28–38k) |

## Predictions

1. **Below ≈ 30k req/s the system keeps up** (achieved ≈ offered).
   - In flight stays small: by Little, L = λ·W with W of a few hundred µs, so about 10 at 20k.
   - p99 is around 1 ms or less, and **0 % of decisions use the fallback**, because in flight never nears 200.
2. **Cost-path saturation at ≈ 33k req/s** (28–38k). Above it the queue grows until more than 200 are in flight, and the
   overload fallback starts deciding.
3. **Fallback crossover ≈ the cost-path saturation.**
   - In the mixed regime the dispatcher hovers near 200 in flight. The fallback share f solves
     1/λ = (1 − f) · 30.5 µs + f · 26.9 µs, so at 35k f ≈ 0.5.
   - There, W = L/λ ≈ 200 / 35k ≈ 6 ms, so p99 is a few ms to about 10 ms.
4. **Knee: the 30k step keeps up and the 40k step does not** (achieved < 99 % of offered). The fallback can only add
   about 4k req/s on top of the cost path.
5. **The generator stays valid** (lag p99 < 1 ms) up to 70k. It does about 2–3 µs of work per request on its own thread.

## Why these could be wrong (what to look at if they are)

- **Snapshot ingestion may cost more than an average message.** It runs the overlay clean-up and a retry of pending
  requests. If the knee comes in lower, this is the first suspect; the pipeline breakdown per actor would show it.
- **The passengers actor now sits on the submit path** (ADR-015 fix 4), with about 4 messages per request. If it
  saturates first, achieved stalls with the dispatcher idle.
- **10 logical cores.** The 4 elevators, dispatcher, passengers, log writer, delay loop, generator (spinning), sampler
  and thread-pool continuations compete for them. Contention would lower every ceiling.
