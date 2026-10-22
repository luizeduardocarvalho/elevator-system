# ADR-006: Cost-based dispatch, sticky assignments, staleness and aging

## Status

Accepted (v2-medium, 2026-10-21). Round-robin dispatch and the optional rebalancer were removed by
[ADR-018](018-prune-superseded-strategies.md); their code is at `v3-full-grid`.

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

**How the terms are computed: route projection.** The elevator is replayed from its snapshot to *now*, `r` is
inserted, and it runs forward with its own scheduling policy and mechanics. Riders board and alight as the passenger
model does. ETA, ΔD and L̂ come out of two projections, with and without `r`; the one without `r` is cached per
snapshot. The estimate is the elevator's own behaviour, not a separate heuristic.

There are two implementations (see "Decision cost" below):
- `RouteProjection` runs the real `Elevator` per passenger. It is the reference oracle.
- `StopProjection` works per stop and per destination. It is what dispatch uses, and it agrees exactly with the
  oracle on random elevators.

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
| up-peak | −0.2 ± 0.4 s (n.s.) | 0.0 ± 0.8 s (n.s.) | ~5 of ~225 passengers |
| down-peak | −0.8 ± 0.5 s | −3.5 ± 2.1 s | ~30 of ~225 (13%) |
| lunch | −0.4 ± 0.4 s (n.s.) | −0.9 ± 1.8 s (n.s.) | ~9 of ~206 |

*Regenerated after the ordering fixes (ADR-015):* a rebalance move now publishes the elevator's snapshot before the
release, so the dispatcher re-dispatches against the current view. Down-peak −0.9 → −0.8 s, lunch no longer
significant: the conclusion below only gets stronger.

On top of Nearest, rebalancing recovers most of Nearest's gap. So rebalancing repairs poor initial decisions, and
cost-based dispatch rarely makes them.

*Correction (found during the M6 projection rewrite).* The first version of this table came from code with a latent
cache bug. The dispatcher cached each elevator's "without r" projection by (version, time), so a derived snapshot
(the same elevator with r removed, as rebalancing builds) could be given the real snapshot's projection. Once the
cache was keyed by snapshot identity:
- the old per-passenger projection and the new per-stop projection give identical m4 results;
- every other results file is unchanged.
The corrected numbers above differ from the first version by at most 1.9 s in p95, and the conclusion is unchanged.

**Consequence: sticky stays the default.** At best rebalancing saves 0.8 s on average, at down-peak, while changing
the displayed elevator for about one passenger in eight. Under destination dispatch that is a real usability cost the
simulation does not model: people walking to the wrong door. Rebalancing stays selectable for buildings that
weigh it differently.

## Decision cost and behaviour under bursts (M6)

Perf testing with real threads showed that a cost decision grew with the backlog. These changes followed, each
measured and each keeping bench decisions identical unless stated.

**1. Per-stop projection (`StopProjection`).**
- *Before.* BenchmarkDotNet put one decision (4 elevators) at 22 µs with an empty backlog and 2.3 ms with 400 queued
  per elevator (3.9 MB allocated). Policy calls were bounded (~850 per decision); the growth was per-passenger
  bookkeeping inside the simulated projection.
- *The rewrite.* Work happens per stop and per destination: the elevator maintains per-destination totals for its
  riders and for each pickup stop, and ΔD is computed by prefix-matching boarding order between the two projections.
  LOOK and FIFO also gained an O(1) bitmask `Decide` (`IFastSchedulingPolicy`).
- *After.* 2.1 µs empty and 80 µs at 400 queued per elevator, 88 µs at 1,600: flat. Allocation ~0.2 MB.
- *Honest bound.* That is O(floors²) per elevator with small constants (≈2·floors stops, each with O(floors)
  per-destination work), not the O(floors) of a pure stop-list formula. It does not depend on the backlog.
- *Proof of equivalence.*
  - A differential property (1,500 random elevators, FIFO and LOOK, capacities 1–1,000, both capacity modes, stale
    snapshots) checks exact equality with the oracle.
  - A second property checks the bitmask and list `Decide` agree on 6,000 random states.
  - All eight results files regenerated byte-identically.
- *Bugs this work found.*
  - Alighting did not unload the new request's own weight when no group got off at that floor (caught by the
    differential test).
  - A latent cache collision in rebalancing: derived snapshots share (version, time). That one changed m4, which
    was corrected above.

**2. Projection horizon of 10 minutes.** With capacity binding, a large backlog needs many trips and the projection
replayed all of them, up to 1 h. Ten minutes bounds the work, and every results file is unchanged.

**3. Never defer in next-trip mode (ADR-010).** If no projection reaches the pickup within the horizon, cost dispatch
ranks by Nearest's effective distance. Before this, deferred requests were retried on every snapshot: 12 ms of
dispatcher time per request in a 1k burst.

**4. Overload fallback (defense in depth).**
- Above **200 requests in flight** the dispatcher uses Nearest, and logs `OverloadModeChanged` on every switch.
- The largest number in flight in any bench run is 92 (FIFO saturating at down-peak); LOOK peaks at 21. So bench
  results are unaffected.
- Cost dispatch alone handles 100 simultaneous requests at p99 ≈ 10 ms. Past a few hundred it is CPU-bound at about
  11k decisions/s, and the fallback is what keeps the latency SLO (docs/results.md).

**5. Pending-assignment overlay.**
- *The problem.* In a burst, the dispatcher evaluated many requests against the same snapshot, which did not show its
  own in-flight proposals. Elevators then rejected as stale: 3.6 proposals per request at 100 simultaneous submitters.
- *The fix.* The dispatcher overlays the pickups it proposed to each elevator on that elevator's snapshot until a
  snapshot at or after the accepted version (now carried by `AssignmentAccepted`) arrives. Result: 1.0 proposals per
  request and 0 stale rejections.
- *Kept cheap.* The overlaid snapshot is maintained incrementally (one merge per proposal), so per-snapshot caches
  keep hitting. It is skipped in overload mode, where the fallback must stay cheap.
- *Effect on results.* next-trip results are unchanged. Defer-mode files m1–m4 move by at most 0.1 s in means
  (deferred retries now see pickups just accepted), and are annotated to reproduce exactly at `d5864a0`.

## Amendment: explaining a decision (shared v3 contract, `a00d01f`)

`DispatchContext.Explain` asks a dispatch policy to report **every candidate's cost** in `DispatchChoice.Candidates`
(null cost = ineligible, ∞). RoundRobin, Nearest and Cost all do; Cost adds an `eta + λ·ΔD + μ·L̂/C` note.
- With `DispatcherOptions.EmitDispatchEvaluations`, the dispatcher logs this as `DispatchEvaluated`, just before
  each `AssignmentProposed`. The TUI's ASSIGN line uses it.
- Explaining never changes a decision. A test removes the extra lines and recovers the plain log byte for byte.
- It is off by default, so bench logs are unchanged.
- Eligibility policies added by Stage 4 (ADR-012) must keep reporting their candidates. Access refusals are logged
  separately as `AccessDenied`.

## Amendment: re-dispatch order (ADR-015)

Pending requests are served oldest first by (t_call, id), and nothing reorders them (VIP priority is a cost term, not
a queue position). An elevator hands a batch of pickups back in (t_call, id) order, after publishing the snapshot that
explains it, so requests already waiting are served before a hand-back and a hand-back keeps each passenger's place.

## Amendment: superseded strategies removed (ADR-018)

Round-robin dispatch and `HysteresisRebalance` (with `--rebalance`, `RebalanceTimer`, `Unassign` and the
`Rebalance` release reason) are no longer on `main`. The measurements above stand, and run at tag `v3-full-grid`.
Dispatch is `cost`, with `nearest` as the overload fallback and the comparison baseline. The spec's `balanceLoad()`
is the predicted-load term of the cost function, so `ElevatorSystem.BalanceLoad()` deliberately does nothing.
