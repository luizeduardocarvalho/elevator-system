# ADR-008: Metric definitions and the two kinds of time

## Status

Accepted (v2-medium, 2026-10-21). Extends the time model of ADR-003.

## Context

Decisions in this project are justified with numbers, so the numbers need exact definitions. Two different clocks
are in play: simulated building time (how long people wait) and real elapsed time (how fast the software answers).
Mixing them would make "100 ms" ambiguous.

## Decision

### Two kinds of time, two types
- **`SimTime`** is integer milliseconds of simulated time. Every passenger metric uses it: wait, ride, journey,
  buckets, sim time.
- **`WallDuration`** is Stopwatch ticks of real time, used only for software performance: assignment latency,
  handler time and soak duration.
- The two types do not convert into each other. A `RealClock` maps wall time to sim time only inside the actor
  runtime, with an explicit speed factor.

### Passenger metrics (sim seconds, delivered passengers)
- **t_call** is the first press. It is kept across re-registrations (left behind, released).
- **wait** = boarding − t_call. Boarding happens when the assigned elevator's doors are fully open, its lantern shows
  the passenger's direction, and there is room.
- **ride** = alighting − boarding. **journey** = wait + ride.
- Passengers not delivered by the end are counted as **unserved**, never in the averages.
- **p95** is nearest-rank (always an observed value, so traceable to a passenger).

### Wait attribution
Every instant of [t_call, boarding] goes into exactly one bucket. Attribution is evaluated per interval between
consecutive events, which is exact and equivalent to infinitely fine ticks. The rules apply in priority order:

| Bucket | When |
|---|---|
| `dispatch` | not yet assigned for the first time |
| `reassignment` | unassigned after a stale reject or a rebalance move |
| `bypassed_full` | unassigned after a capacity reject, **or** assigned to an elevator that is full (next-trip, ADR-010). Before ADR-018 also after a full-load release in defer mode |
| `blocked` | left behind at an open door and still waiting, or the assigned elevator is in maintenance / rejected for maintenance |
| `finishing_door_cycle` | assigned while the elevator was already stopped at a floor; until that stop ends |
| `serving_others` | the assigned elevator is at a stop that began after the assignment (another floor, or this floor in the wrong direction) |
| `idle_start` | the assigned elevator is idle with doors closed |
| `travel_toward` | the assigned elevator is moving and getting closer, or opening its doors at the passenger's floor |
| `travel_away` | the assigned elevator is moving away (finishing a sweep first) |

- **Reject reasons map to buckets:** stale → `reassignment`; maintenance → `blocked`; capacity → `blocked` if already
  left behind, else `bypassed_full`.
- **Verification:**
  - the buckets of every passenger sum exactly to their wait (a property over random buildings and strategies);
  - hand-computed traces pin the attribution: v1-basic, and the capacity scenario in both modes.

### Comparing strategies
- **Common random numbers:** the seed alone fixes the passengers, so every strategy sees the same people.
- **Each cell** is the mean over seeds ± 95% CI (Student t, n − 1 degrees of freedom).
- **Δ columns** are paired per-seed differences against a named baseline, with their own Student-t CI. ✱ marks an
  interval that excludes 0.
- **Held-out seeds.** Weights were tuned on seeds 1–20 and reported on seeds 21–40.

### Software performance (wall clock)
- **Assignment latency** = from the `AssignRequest` call to the accepted assignment. It includes queueing in the
  bounded request channel. Recorded in an HdrHistogram (p50/p95/p99/max).
- **Lost** = no assignment within the timeout. **Duplicated** = a request owned by two elevators at once, checked on
  the log.
- **Memory** = managed heap after a full GC, sampled every second after warm-up. Growth compares the last quarter of
  the samples with the first.
- **Warm-up** (JIT, thread pool) runs on a throwaway system first.
- **Wall-clock numbers are only taken on a quiet machine;** the perf tests run in a non-parallel collection.

## Consequences

- **Each claim in an ADR points to a results file**, and each results file records the exact command that produced
  it.
- **The buckets turn "LOOK is better" into a mechanism.** FIFO's down-peak wait is 95 s of `travel_away`; under LOOK
  it is 4.8 s.
- **Nearest-rank p95 on 200–450 passengers per run** is coarse but honest. The CI comes from the spread across seeds,
  not from within a run.
- **Wall-clock results depend on the machine and its load.** They are reported with core count, runtime and OS
  version (`perf` writes these), and re-measured when the machine is quiet.

## Amendment: new release reasons (shared v3 contract, `a00d01f`)

Until Stage 4 says otherwise (ADR-013):
- a pickup handed back with reason `Fault`, `Emergency` or `Maintenance` counts as `reassignment` time, by the
  existing rule that every release other than full-load bypass is a reassignment;
- time assigned to an elevator in `ElevatorState.Maintenance` counts as `blocked`.

Ops metrics (maintenance time, emergency stops and their duration, faults, time to detect and to recover) will be
computed from the new events, from the log alone, like every other metric.

## Amendment: out-of-service time (Stage 4, ADR-013)

Settled by [ADR-013](013-maintenance-emergency-watchdog.md):
- **Releases** for maintenance, emergency or fault count as `reassignment`, as above. In practice that time is close
  to zero: the dispatcher reassigns at once.
- **Rejections** for emergency or fault count as `blocked`, like maintenance rejections ("an elevator out of service
  refused it").
- **Time assigned to a stuck elevator** counts as `blocked`. Stuck intervals come from `FaultCommandApplied`
  (DurationMs, or until Unstick), and attribution splits intervals at a timed stuck's end, so buckets still sum
  exactly to the wait.
- **The wait penalty** of out-of-service reassignments is the `reassignment` + `blocked` bucket time. It is reported
  in the Operations section of `run` and `bench`.
- **Passengers refused by an access policy** (ADR-012) are neither delivered nor unserved. They are a separate count.

