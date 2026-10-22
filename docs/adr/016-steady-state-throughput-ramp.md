# ADR-016: A steady-state, open-loop throughput ramp

## Status

Accepted (2026-10-22). Complements ADR-005's burst measurements; building capacity stays in the simulator (ADR-014,
traffic-intensity sweep).

## Context

Every earlier performance number came from **bursts**: N callers released at once, latency until each is assigned.
- **The "≈ 57k req/s" figure is derived.** It comes from burst drain times, and was never measured as a sustained rate.
- **Bursts can't answer "how many requests per second, sustained?".** That needs an *open-loop* load at a fixed
  offered rate, with latency measured from when each request was *meant* to be sent (no coordinated omission).
- **The rates must be stepped up until the system stops keeping up**, to find the knee.

A real building can't absorb thousands of requests per second. The elevators fall behind physically, the pickup
backlog grows without bound, and a long run would measure that queue, not the software. So the ramp measures the
**assignment service in a steady state**. The building's work must stay realistic and constant while the request rate
rises.

## Decision

**Seed from a real peak.** `PeakMoment.Find(scenario)` runs the simulator and picks the earliest moment with the most
outstanding work (riders plus accepted pickups); for lunch that's 15 across the 4 elevators. Each step:
- builds a fresh simulator, runs it to that moment, and **hands its handler objects over** to the actor runtime
  (`SimEngine.HandOver`, `ElevatorSystem.FromEngine`);
- moves ownership once, before any thread starts. The engine refuses to run afterwards, so no handler ever has two
  owners;
- delivers messages in flight with their remaining delay, and drops future passenger arrivals (the generator replaces
  the scenario's traffic).

**Steady state:**
- **The building's clock is frozen** at that moment (`RealClock` speed 0): no phase ever completes, so the elevators
  keep their positions, riders and pickups.
- **Every generated request is retired as soon as it is accepted** (`PassengerRetired`, sent by the runtime only with
  the perf-only `RetireOnAccept` option). The elevator drops the pickup without releasing it, and the passengers handler
  tells the dispatcher through the `RequestPickedUp` it already handles when someone boards. The backlog, which sets the
  cost of every dispatch decision, stays exactly the seeded one.
- **One honest caveat:** an elevator's *phase* can change once at the frozen instant. Doors closing at a floor reopen
  for a pickup there, and an idle elevator commits to a direction. With no time passing, nothing moves further. The
  test pins the backlog, not the phase.
- `--building-speed 1` runs the building in real time instead, for comparison.

**Open-loop load** (`ThroughputRamp`):
- **One generator thread** spins on the Stopwatch. Request i is due at t0 + i/rate and is submitted then, whatever
  happened to the previous ones.
- **It never awaits.** A full request queue becomes latency, not a slower generator.
- **Latency** is measured from the **intended** send time to the accepted assignment. The acceptance timestamp is taken
  in the runtime when the acceptance arrives (`Assignment.CompletedAt`), not when a continuation gets to run.
- **Steps:** 1k, 2k, 5k, 10k, 15k, 20k, 30k, 40k, 50k, 60k and 70k req/s. Each has 5 s of warm-up (discarded) and
  30 s measured. The ramp stops after the first step whose achieved rate is below 99 % of offered.
- **Repeats:** 3, and each rate reports its median-p99 repeat.

**Reported per step** (`RampAnalysis`, unit-tested on synthetic timestamps):
- offered and achieved rate;
- latency p50, p99, p99.9 and max;
- the in-flight count, sampled independently every ~1 ms, against Little's law λ·W (a gap above 5 % is flagged);
- the share of dispatch proposals made by the overload fallback;
- GC collections per generation;
- the generator's own lag. A step is **invalid** if its p99 exceeds 1 ms, because then the offered rate was not
  actually offered.

**Predictions before measuring** ([perf-ramp-predictions](../results/perf-ramp-predictions.md), committed first):
- cost-path saturation ≈ 33k req/s;
- the Nearest fallback's ceiling ≈ 37k;
- the knee between the 30k and 40k steps.

## Consequences

### Measured (2026-10-22, commit 72b5707, [perf-ramp](../results/perf-ramp.md))

**Headline.** The highest valid step is **40k req/s**: achieved 40k, p99 7.7 ms, 12 in flight, 0.2 % of decisions by
the fallback. Achieved stayed within 1 % of offered at every step up to 70k, so **no throughput knee was found**. From
50k up, though, the generator itself fell behind (lag p99 > 1 ms), so those rates were not truly offered.

| Prediction | Measured | Verdict |
|---|---|---|
| 1. Below ≈ 30k it keeps up; p99 ≈ 1 ms; 0 % fallback | p99 ≤ 1.03 ms to 20k, 2.1 ms at 30k; 0 % fallback | Holds |
| 2. Cost-path saturation ≈ 33k (28–38k) | 40k: 12 in flight, 0.2 % fallback. 50k: 1 of 3 repeats held on the cost path alone (13 in flight), 2 tipped into the fallback (475 and 886 in flight) | **Wrong: ≈ 50k**, about 1.5× the prediction |
| 3. Fallback share f ≈ 0.5 at 35k | 0 % at 30k, 6 % at 50k, 48 % at 60k, 66 % at 70k | **Wrong:** the crossover sits near 60k |
| 4. Knee: 30k keeps up, 40k doesn't | Achieved ≥ 99 % of offered at every step to 70k | **Wrong:** no knee within the ramp |
| 5. Generator valid (lag p99 < 1 ms) to 70k | Valid to 40k; lag p99 1.3–5 ms from 50k | **Wrong:** one generator thread tops out near 40–50k |

**Why the predictions were low.** They priced a dispatcher message at 5.1 µs, taken from the 10k burst. The ramp
implies at most about 4 µs: 50k req/s × 5 messages per request is ≤ 20 µs per request. The likely reason, not yet
measured per actor, is that a burst sits on a large backlog, which makes snapshot ingestion and pending retries
costlier. The ramp holds the backlog at 15. Burst-derived costs are an upper bound for the steady state, not an
estimate of it.

**Above 50k** the system settles rather than diverging. In flight levels off at about 1,250–1,320 and Little's law
agrees within 1 %, with p99 at 28–46 ms. The overload fallback is what keeps it there, as ADR-005/006 intended. This
regime also costs about 400 gen-2 GCs per step, against 0–2 below it, because queued requests survive long enough to
be promoted.

**Noise, recorded rather than smoothed:**
- **Machine load.** The load average was 2.8 at the start and 11.8 at the end on 10 cores. The end figure includes
  the ramp's own spinning generator and sampler, plus Chrome and WindowServer.
- **Outlier steps.** Some steps have a large generator lag:
  - 2k, repeat 2: a 6.7 s stall;
  - 2k, repeat 3: 15 ms;
  - 15k, repeat 1: 19 ms.

  These lags come with their latency spikes. The validity flag marks them, but the median-p99 rule still picked an
  invalid repeat at 2k.
- **Little's law at low rates.** It is flagged ⚠ below about 10 in flight, where a ~1 ms sampler can't resolve a mean of
  0–4. It agrees within 1 % once the queue is real (60k, 70k).

**Follow-ups, not done:**
- Several generator threads, to offer valid load above 40k and find the real knee.
- A per-actor breakdown at 40–50k, to confirm where the 1.5× came from.
- Picking the median *valid* repeat.

- **Test-only code paths are explicit and named**:
  - `PassengerRetired` and `RetireOnAccept` (perf only, never set for a real system);
  - `SimEngine.HandOver` and `ElevatorSystem.FromEngine` (a general seam, used here);
  - a frozen `RealClock`.
- **Bursts stay.** They measure a different question (how fast a sudden crowd is absorbed) and remain the spec-SLO gate.
