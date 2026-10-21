# ADR-009: Destination dispatch and the capacity model

## Status

Accepted (v2-medium, 2026-10-21).

## Context

The spec's Medium `Request` has `pickupFloor`, `destinationFloor`, `direction` and `timestamp`. A request that
carries its destination before pickup is **destination dispatch**: the passenger enters a floor on a hall keypad
and is told which elevator to take. v1 used conventional up/down buttons, with the destination pressed inside.

Capacity is what actually limits a building at peak: a full elevator cannot pick anyone up. Without it, up-peak
results are unrealistically good.

## Decision

**Destination dispatch in multi-elevator mode.**
- Each request is assigned to one elevator, and the passenger boards **only that elevator**, when its lantern
  shows their direction.
- Requests for the same floor and direction on one elevator share one stop. That is hall-call dedup at the
  elevator.
- Submissions are idempotent by request id while in flight.
- The v1 single-elevator API (`RequestElevator(floor, direction)` + `SelectFloor`) stays, as a pickup without a
  known destination. The v1 golden trace still passes unchanged.

**Capacity in load units** (a person is 1; Stage 3 freight can use larger loads). Default capacity 10.

**Full-load bypass at 80% of capacity** (configurable `bypassThreshold`). At or above it, the elevator:
- hands every pending pickup back to the dispatcher (`AssignmentReleased(BypassedFull)`);
- serves only its riders;
- rejects new assignments (`Capacity`).

**Partial boarding.** Passengers board in arrival order, up to the hard capacity. Anyone who does not fit is
**left behind**:
- they re-register with the **same request id and the same t_call**, so their wait is measured from the first press;
- `leftBehind` is counted per request.

**Two separate wait buckets** (ADR-008):
- `bypassed_full`: the assigned elevator filled up elsewhere and handed the pickup back;
- `blocked`: the elevator came, but was full at the door (or the assigned elevator is in maintenance).

## Consequences

- **Invariants checked as properties** over random buildings (1–5 elevators, 3–20 floors, capacity 1–8, thresholds
  0.5–1.0, snapshot and dispatch latency, every strategy):
  - load ≤ capacity at all times;
  - only the assigned elevator is boarded;
  - boarding follows arrival order per floor;
  - no request is owned by two elevators;
  - everyone, including anyone left behind or bypassed, is served exactly once.
- **Mutation checks.** The properties catch each of these deliberate bugs on the first or third generated case:
  ignoring capacity, boarding in reverse order, and boarding a non-assigned elevator.
- **Capacity makes dispatch matter at up-peak.** Under LOOK+Nearest, 5.8 ± 2.8 people per run are left behind at the
  lobby. CostBased's predicted-load eligibility brings that to 0 (ADR-006).
