# ADR-010: Next-trip assignment under full load

## Status

Accepted (v2-medium, 2026-10-21). Supersedes the full-load part of [ADR-009](009-destination-dispatch-and-capacity.md):
releasing pickups at the bypass threshold and deferring requests no elevator can take. That behaviour stayed
selectable as capacity mode `defer` until [ADR-018](018-prune-superseded-strategies.md) removed it (tag `v3-full-grid`).

## Context

Under ADR-009, a full elevator handed every pending pickup back, and cost dispatch treated an elevator that would
be full at pickup as ineligible. When every elevator was full, the request was *deferred*: the passenger had no
elevator until one freed up. Perf testing made the consequences concrete:
- **Deferral is not what destination-dispatch panels do.** They always name an elevator.
- **Deferral is extra state**: a deferred queue retried on every snapshot.
- **The latency SLO measured physics.** With realistic capacity, "assignment latency" for a deferred request was
  seconds of waiting for seats, not software.
  - 1,000 simultaneous requests into 40 seats: about 970 deferred, p99 8 s.
  - Disabling capacity in perf would hide that rather than fix it.

## Decision

Capacity mode **`next-trip`** is the default (`building.capacityMode`, `--capacity-mode`).

- **Mechanics.**
  - A full elevator (load ≥ bypass threshold) **keeps** its pickups.
  - It only serves its riders: the scheduling policy sees no pickups while it is full, and no pickup is answered or
    reopened for.
  - When riders get off and it drops below the threshold with the doors open, a matching pickup on that floor is
    answered there; otherwise the elevator comes back on a later pass.
- **Acceptance.** An elevator rejects for capacity only a request that can never fit (load > capacity).
  Maintenance and staleness rejects are unchanged, and capacity and maintenance still beat aging.
- **Dispatch never defers for capacity.**
  - Cost-based dispatch scores a full elevator with the ETA its own projection gives, which includes waiting for the
    next drop-off cycle. That is the same mechanics replayed, so no special case is needed.
  - If no projection reaches the pickup within its horizon (a huge backlog), cost dispatch ranks by Nearest's
    effective distance instead of deferring.
  - Nearest and RoundRobin prefer elevators with room and fall back to full ones.
- **When the prediction fails** (the elevator comes but is full at the door), the passenger is left behind and
  re-registers with the same id and t_call. That is the existing re-assignment path (ADR-009).
- **Attribution.** While a passenger's assigned elevator is full, their wait goes to `bypassed_full` (ADR-008).
- **`defer` mode is kept** for comparison. Every M1–M4 results file was produced in it and is annotated to say so.

## Consequences

**Measured on held-out seeds 21–40** ([m5-capacity-modes](../results/m5-capacity-modes.md), paired against
LOOK+Cost/defer):

| Profile | LOOK+Cost next-trip vs defer: Δ wait | Δ p95 | LOOK+Nearest next-trip vs defer: wait / p95 |
|---|---|---|---|
| up-peak | −0.6 ± 0.4 s | −1.2 ± 1.4 s (n.s.) | 19.3 → 20.7 s / 46.6 → 59.4 s |
| down-peak | −0.2 ± 0.2 s (n.s.) | −0.1 ± 0.9 s (n.s.) | 28.3 → 29.3 s / 84.1 → 87.7 s |
| lunch | 0.0 (capacity never binds) | 0.0 | unchanged |

**Next-trip is only as good as the dispatcher's view of the next trip.**
- *Cost dispatch*: its projection sees the next trip, so it names a full elevator only when that really is fastest.
  It matches or slightly beats defer.
- *Nearest*: it names an elevator that then fills up at the lobby. In defer mode that elevator hands the passenger
  to someone else; in next-trip mode they wait for its next trip, and up-peak p95 gets 13 s worse.
- So next-trip and cost-based dispatch go together: the default is LOOK + Cost + next-trip.

**Under overload the latency SLO now measures software.** Every request is named an elevator at once.
- 1,000 simultaneous requests: all assigned, 0 lost (perf, docs/results.md).
- This also removed a retry storm: deferred requests being re-evaluated on every snapshot cost 12 ms of dispatcher
  time per request.

**The projection horizon became a real parameter** (10 minutes of sim time, `StopProjection.DefaultHorizonMs`).
- With capacity binding, clearing a large backlog takes many trips, and the projection replays all of them up to the
  horizon.
- Ten minutes bounds that work. All eight results files regenerate byte-identically with 10 minutes instead of 60, so
  no decision in any bench run depends on the longer horizon.
