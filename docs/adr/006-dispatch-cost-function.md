# ADR-006: Cost-based dispatch, sticky assignments, staleness and aging

## Status

Accepted (v2-medium, 2026-10-21).

## Context

With several elevators, every request must go to exactly one of them. The spec asks for "closest elevator, same
direction priority", load balancing (`balanceLoad()`), and minimising wait time. Three dispatchers stay
selectable forever (`--dispatch`):

- **RoundRobin**: turns, ignoring position. The reference lower bound.
- **Nearest**: the spec's rule, as an effective distance. An elevator on its way in the request's direction counts
  |Δfloors|; otherwise it counts the distance to the end of its sweep and back. This is the baseline.
- **CostBased**: this ADR.

Under LOOK, Nearest still loses time in identifiable places ([m2-look](../results/m2-look.md)):
- At up-peak it piles lobby requests onto one elevator, leaving 5–6 people per run behind at the door.
- At down-peak its p95 is 85 s.

The dispatcher works from snapshots, which can be stale. Under destination dispatch (ADR-009) the assignment is
shown to the passenger on the hall panel, so moving it afterwards has a human cost.

## Decision

**cost(e, r) = ETA + λ·ΔD + μ·L̂/C**, choosing the lowest. Ties go to the lowest id.

- **ETA**: seconds from now until `e` would pick `r` up.
- **ΔD**: total extra seconds that everyone already riding in or assigned to `e` would spend before their drop-off if
  `e` also takes `r`. Each passenger's delay counts once.
- **L̂/C**: predicted load at pickup over capacity, where L̂ = current riders + pickups before `r` − drop-offs before
  `r`. This is the spec's `balanceLoad()`.
- **Ineligible** (∞):
  - maintenance;
  - full now;
  - `r` would not be picked up;
  - at pickup the elevator would already be bypassing (L̂ ≥ threshold·C);
  - `r` would not fit (L̂ + load > C).

  This rule mirrors the mechanics exactly. An earlier version (`L̂ + 1 > threshold·C`) made every elevator
  permanently ineligible at capacity 1 with threshold 0.5. A random-building property test found that deadlock
  before any bench ran.

**How the terms are computed: route projection.** `RouteProjection` rebuilds the elevator from its snapshot
(`Elevator.FromSnapshot`), replays it to *now*, inserts `r`, and runs it forward with the elevator's own scheduling
policy and mechanics. Riders board and alight exactly as the passenger model does. ETA, ΔD and L̂ fall out of
two projections, with and without `r`. The projection without `r` is cached per snapshot version.

The estimate is the real code, not a separate heuristic. A test checks that the projected pickup time equals the
time the real elevator answers.

**Weights:** λ = 0.5 and μ = 10 s, chosen by a sweep ([m3-cost-weights](../results/m3-cost-weights.md), seeds
1–20, LOOK, vs LOOK+Nearest).
- λ = 0.5 is best or tied for best in every profile. λ = 0, ETA only, costs riders journey time: +1–2 s journey.
  λ = 2 over-protects riders and pushes waits back up.
- μ has no measurable effect between 0 and 10, and μ = 30 hurts in every profile. Load balancing is achieved by
  the predicted-load eligibility rule above, not by μ. μ stays in the formula at a weight that does no harm.
- The results below were then measured on **held-out seeds 21–40**, so they are not fitted to the tuning seeds.

**Sticky assignments.** An accepted assignment is not moved, except:
- when the elevator hands it back (full-load bypass, ADR-009);
- or by the optional rebalancer (below).

Destination dispatch shows the passenger which elevator to take, so moving it silently would send them to the wrong
door.

**The elevator has the final say: accept/reject, in priority order.**
1. **Maintenance** rejects always win.
2. **Capacity** rejects always win: full means full, even for an aged or forced request.
3. **Stale**: reject if the elevator's own projected pickup time is more than 10 s later than the dispatcher's
   estimate. The dispatcher's snapshot can lag (configurable `latency.snapshotMs`), or it may have committed several
   requests to the same elevator from one snapshot. Two exceptions:
   - **Aging**: a request waiting ≥ 60 s since t_call can no longer be rejected as stale.
   - **Forced**: if every eligible elevator rejected a request as stale, the dispatcher re-proposes it as forced.
   Aging only removes staleness rejects; it never overrides capacity or maintenance.
4. Otherwise accept.

**Rejected requests** exclude that elevator until its next snapshot and are re-dispatched oldest-first (the aging
order). Time spent unassigned after a reject goes to the `reassignment` wait bucket (ADR-008).

## Consequences

Held-out seeds 21–40 ([m3-cost](../results/m3-cost.md), [m3-cost-vs-nearest](../results/m3-cost-vs-nearest.md)).
All Δ are paired; every interval excludes 0.

| Profile | LOOK+Nearest wait / p95 | LOOK+Cost wait / p95 | Δ wait vs LOOK+Nearest | Δ p95 | Δ journey | Left behind |
|---|---|---|---|---|---|---|
| up-peak | 19.3 / 46.7 s | 16.4 / 39.1 s | −2.9 ± 0.7 s | −7.5 ± 2.8 s | −4.8 ± 0.5 s | 5.8 → 0.0 |
| down-peak | 28.3 / 84.1 s | 20.8 / 53.3 s | −7.5 ± 1.0 s | −30.8 ± 3.1 s | −9.5 ± 1.0 s | 0 → 0 |
| lunch | 20.9 / 79.2 s | 13.8 / 38.4 s | −7.1 ± 0.8 s | −40.8 ± 4.3 s | −8.2 ± 0.8 s | 0 → 0 |

Against the FIFO+Nearest baseline: −10.0 / −230.4 / −81.4 s average wait.

- **The bench caught a bug that the unit tests had baked in.** The first M3 run showed LOOK+Cost *worse* than
  LOOK+Nearest (37.5 vs 19.2 s at up-peak), with 480 stale rejections at zero latency.
  - The cause: the projection rebuilt an elevator at its snapshot's own time. An idle elevator's snapshot dates from
    when it last changed, so the projection had it "start moving" minutes ago and promised a near-zero ETA.
  - The elevator then rejected the assignment as stale, and the request fell to a busy elevator.
  - A unit test had asserted that wrong number. The test now states the correct behaviour (an idle elevator assigned
    at t = 100 s picks up at 105 s, not 5 s), and the projection replays snapshots forward to now.
  - After the fix: zero stale rejections at zero latency.
- **Cost is higher than Nearest.** Each decision runs two route projections per candidate elevator, against an O(1)
  distance. That is microseconds to a millisecond per decision; the perf SLO is checked in M6.
- **Stale snapshots are measurable.**
  - Lunch with `latency.snapshotMs: 3000`, seed 1: 454 stale rejections and 2 forced assignments, everyone served.
  - The `reassignment` bucket shows the cost. This is the scenario the staleness rule exists for, and it can be
    reproduced exactly in the sim.

## Rebalancing (optional, `+rebalance`)

**Decision.** Every 5 s, `HysteresisRebalance` reconsiders each assigned, not-yet-picked-up request using the same
cost function. The current elevator is evaluated with the request removed from its snapshot, so both sides compare
"adding r" on equal terms. A request moves only if all of these hold:
- the best other elevator is cheaper by more than **H = 15 s** (hysteresis, so estimate noise never makes requests flap);
- the current elevator is **not within stopping distance**: its projected pickup is more than one floor of travel
  plus the door opening away (the commit point);
- the request has moved **fewer than 2 times**.

A move is `Unassign` → `Released(Rebalance)` → re-dispatch → `Accepted`. Time between the release and the new
acceptance goes to the `reassignment` bucket.

**Measured** ([m4-rebalance](../results/m4-rebalance.md), held-out seeds 21–40, paired against sticky LOOK+Cost):

| Profile | Δ wait avg | Δ wait p95 | Moves per run (seeds 21–25) |
|---|---|---|---|
| up-peak | −0.3 ± 0.4 s (n.s.) | −0.4 ± 0.5 s (n.s.) | ~6 of ~225 passengers |
| down-peak | −0.7 ± 0.5 s | −2.3 ± 1.8 s | ~37 of ~225 (16%) |
| lunch | −0.3 ± 0.5 s (n.s.) | −1.3 ± 2.0 s (n.s.) | ~10 of ~206 |

On top of Nearest, rebalancing recovers most of Nearest's gap. Lunch wait goes 20.9 → 15.3 s, against 13.8 s for
sticky Cost. So rebalancing repairs poor initial decisions, and cost-based dispatch rarely makes them.

**Consequence: sticky stays the default.** At best rebalancing saves 0.7 s on average, at down-peak, while changing
the displayed elevator for one passenger in six. Under destination dispatch that is a real usability cost the
simulation does not model: people walking to the wrong door. Rebalancing stays selectable for buildings that
weigh it differently.
