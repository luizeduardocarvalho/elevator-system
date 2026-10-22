# ADR-012: Eligibility policies and elevator types

## Status

Accepted (Stage 4, Hard; branch `stage4-hard`, 2026-10-21). VIP priority superseded by ADR-015 (a cost term, not a queue position). Builds on ADR-006 (cost-based dispatch), ADR-009/010
(capacity) and the shared v3 contract (`ElevatorSnapshot.TypeName`, `.ServedFloors`, `AccessDenied`,
`DispatchEvaluated`).

## Context

The spec's Hard level asks for elevator types (express, local, freight) and for floor restrictions and VIP access.
Until now every elevator was identical: same speed, same doors, same capacity, every floor. Three things had to stay
true while adding variety:
- **Byte identity.** With the default config, every bench result and event log must regenerate identically
  (contract-v3).
- **Cost dispatch must keep predicting each elevator exactly.** `StopProjection` replays an elevator's own mechanics;
  if an express is faster or skips floors, the projection must know.
- **Eligibility is a yes/no question, not a cost.** A passenger without a badge must never be taken to the penthouse,
  however cheap that would be. Folding it into the cost function (a big penalty) would make it a trade-off.

## Decision

### Elevator types are configuration
- **`ElevatorType(Name, Timing, Capacity, ServedFloors)`**: floor travel and door times, capacity in load units, and a
  served-floors mask (bit f = floor f; `FloorMask.All` = every floor, also above 63).
- **YAML.** `building.types` (missing fields default to the scenario's timing and capacity, and to every floor) and
  `building.fleet: [local, local, express, freight]`. Served floors accept `1` and ranges like `10-20`.
- **Default.** No types → `Elevators` × `standard`, built from the scenario's timing and capacity. That is exactly the
  v2 building, so nothing changes.
- **Mechanics.** Each elevator runs with its own timing and capacity. It starts at its lowest served floor. It never
  stops at a floor it does not serve: arriving at one, it carries on to the next served floor and decides there.
  Every stop it can have (car calls, pickups) is a served floor, so one always lies ahead. A pickup or car call at an
  unserved floor is a programming error (it throws): the access pipeline prevents it.
- **Snapshots carry the type** (`TypeName`, `ServedFloors`, and a new non-contract `Timing`). `StopProjection` uses
  the snapshot's timing and served floors, so cost dispatch predicts an express as an express. `RouteProjection` (the
  oracle) gets them through `Elevator.FromSnapshot`. The equivalence property now draws random types (timing, served
  floors) and loads above one unit.

### Eligibility: a composable access pipeline, before cost
- **`IAccessPolicy`**: `Deny(request, elevator) → reason or null`, plus an optional `Priority(request)`. Pure; it
  depends only on the elevator's configuration (id, type, served floors, capacity), never on where it is. So the answer
  for a request never changes, and the dispatcher evaluates the pipeline **once per request**.
- **`AccessPipeline`** composes policies in order. An elevator is allowed when every policy allows it; the first denial
  gives the logged reason. Order changes only which reason is reported, never the outcome (a table test checks it with
  the order reversed).
- **Policies** (in pipeline order):
  1. `served-floors`: the type serves both the pickup and the destination;
  2. `capacity`: the request's load fits the type's capacity (a pallet goes to the freight elevator);
  3. `floor-restriction`: restricted floors need a minimum access level, for pickup and destination (`access.restrictedFloors`);
  4. `vip`: optional dedicated elevators that serve VIPs only, and optional VIP priority (`access.vip`).
  The first two are always on and inert by default (every floor served, every unit load fits); the last two only when
  configured. `Request` gains `Vip` and `AccessLevel` (default false / 0); scripted passengers accept `vip`,
  `accessLevel` and `load`.
- **In the dispatcher.**
  - Each denial is logged once, as `AccessDenied(request, elevator, policy, reason)`.
  - A denied elevator is never a candidate: not for the dispatch policy, not for a forced assignment after stale
    rejections, not for rebalancing, not for `findBestElevator`. The policy itself is unchanged; it simply never sees
    the elevator (cost ∞).
  - `DispatchEvaluated` still lists every elevator: denied ones with no cost and the policy as note; out-of-service ones
    with the mode as note.
  - **Refusal.** If every elevator denies, the dispatcher logs `AccessDenied(request, null, policy, reason)`, drops the
    request and tells the passenger (who leaves) and the client (`AssignRequest` throws `RequestRefusedException`). The
    passenger is counted as **refused by policy**, never as unserved/lost. Without this, a request no elevator may take
    would wait forever.
  - ~~**VIP priority** orders the pending requests by (priority, t_call, id): a VIP waiting for an elevator to come back
    into service gets it before anyone who called earlier. With no VIP the order is the old (t_call, id).~~
    **Superseded by ADR-015:** pending requests are always served by (t_call, id); VIP priority is a cost term (a VIP
    request is not charged for the delay it adds to others), not a queue position.

### Loads above one unit (found by the properties)
The invariant properties, drawing freight loads, found three livelocks that one-unit loads can never produce:
- a load left behind at an open door, re-dispatched to the same elevator, was answered again at the same instant;
- LOOK serves "a pickup right here" first, so an elevator with riders reopened forever for a load it could not fit;
- passengers skipped a heavy request and let a lighter one behind board, so the boarders were no longer a prefix of the
  arrival order, which `StopProjection`'s per-group added delay assumes.

Decisions:
- a new pickup at an open door that cannot fit is not answered at once;
- the full-load bypass of ADR-010 is generalised per pickup: a stop whose heaviest load does not fit the room left is
  hidden from the scheduling policy until riders get off (then the first in line always fits; once empty everything
  fits, by the capacity policy);
- boarding is strictly first come, first served.

With one-unit loads, "does not fit" means "full", which already bypasses everything, so all three are inert by default.
They are mirrored in `StopProjection`; removing the mirror makes the equivalence property fail.

## Consequences

- **Byte identity holds.** All eight bench results files (m1–m5) and the four default event logs (`up-peak`,
  `down-peak`, `lunch`, `v1-basic`) regenerate identically after every milestone.
- **Cost dispatch exploits the types.** On the typed fleet (two locals, an express serving 1 and 11–20 at 1 s/floor, a
  slow freight; restricted penthouse) — [h2-fleet](../results/h2-fleet.md), held-out seeds 21–40:

  | Strategy | Wait avg | Wait p95 | Δ wait avg vs Nearest | Δ p95 |
  |---|---|---|---|---|
  | LOOK + RoundRobin | 44.6 ± 1.9 | 117.4 ± 4.8 | +21.8 ± 1.8 | +35.4 ± 6.2 |
  | LOOK + Nearest | 22.8 ± 1.3 | 81.9 ± 4.0 | – | – |
  | LOOK + Cost | **14.0 ± 0.8** | **39.7 ± 3.3** | −8.8 ± 1.0 | −42.3 ± 3.0 |

  Nearest counts floors, so it treats the 1 s/floor express like the 2.5 s/floor freight. Cost dispatch projects each
  elevator with its own timing and served floors (ADR-006), so the gap to Nearest is wider than on a uniform building
  (lunch, m5: 13.8 vs 21.0 s). 16.9 passengers per run (6.6 %) are refused: generated visitors have no badge for the
  penthouse. They are reported apart from the waits and never counted as lost.
- **Eligibility can never be traded for cost.** A denied elevator is invisible to every dispatch path. The properties
  check it over random fleets: VIP-dedicated elevators carry VIPs only, restricted floors are never served to the
  unauthorised, doors never open at an unserved floor, and every request is delivered or refused.
- **Static eligibility is a simplification.** Access depends on configuration, not on time (no "the penthouse is open
  9–5"). A time-dependent policy would need re-evaluation when the rule changes; the once-per-request evaluation is
  where that would go.
- **Known gaps.**
  - **Wait attribution uses the building's capacity.** The `bypassed_full` bucket compares an elevator's riders with
    the building's capacity from `SimulationStarted`, which has no per-type capacity. It also counts riders, not load
    units. Buckets still sum exactly to the wait, but on a typed fleet the split between `bypassed_full` and the travel
    buckets is approximate. Logging the fleet (per-elevator capacity) in `SimulationStarted` would fix it. That changes
    an event's schema, so it is left to the merge (a contract decision), not made here.
  - **VIP waits cannot be measured from the log.** `PassengerArrived` does not carry the VIP flag. VIP behaviour is
    verified by tests (tables, the dispatcher's priority order, the properties), not by a bench number.
