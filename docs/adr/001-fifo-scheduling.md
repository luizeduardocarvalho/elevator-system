# ADR-001: FIFO hall-call scheduling with real elevator mechanics

## Status

Accepted (v1-easy, 2026-10-21)

## Context

The spec's Easy level asks for "a simple FIFO (First In, First Out) scheduling algorithm" for one elevator
serving floors 1–10. Read literally, FIFO means serving every stop strictly in arrival order. A real elevator
does not work that way: it never carries a rider past the floor they pressed, and riders only step in when the
direction lantern matches where they want to go. We want the system to behave like a real elevator, and we want
FIFO to be an honest baseline that later strategies (LOOK, collective control, cost-based dispatch) can be
measured against.

We considered three readings:

1. **Strict FIFO.** Every stop, car calls included, is served in queue order. This can carry a rider away from
   their floor (pressed 9, but the elevator goes down to 2 first), which no real elevator does.
2. **FIFO plus pick-up on the way.** Order comes from the queue, but the elevator also stops for same-direction
   hall calls it passes. That is collective control, roughly half of LOOK. The v1 baseline would then hide
   most of the gain we want to measure in v2.
3. **FIFO hall calls with real mechanics.** Chosen.

## Decision

Split responsibilities between mechanics, which are fixed and policy-independent (in `Elevator.Domain`), and the
policy, which only answers "which floor next?" (`ISchedulingPolicy.NextGoal`, in `Elevator.Policy`).

Mechanics:
- The elevator always stops for a car call at a floor it reaches.
- It stops at the policy's goal.
- On stopping, it lights a lantern. Only the hall call in that direction is answered, so only riders going that
  way board.
- It does not stop for hall calls it passes. If it happens to stop at a floor for another reason and a hall call
  there matches the lantern, those riders board, as they would in a real building.

`FifoScheduling`:
1. Riders on board come first. While car calls lie ahead in the committed direction, the goal is the farthest one.
   Otherwise the goal is the nearest car call, with ties broken towards the lower floor.
2. Otherwise the goal is the floor of the **oldest** hall call, regardless of distance or direction.
3. Otherwise there is no goal, and the elevator stays idle where it is (no homing).

There is no starvation: car calls come only from riders who boarded (a finite number), and every hall call
eventually becomes the oldest. The FsCheck property `Every_request_is_served_exactly_once_and_correctly`
checks this end to end.

## Consequences

- The baseline is realistic and simple to defend. Its weak spot is easy to show:
  in `scenarios/v1-basic.yaml`, passenger 3 (floor 7, going up) waits 63 s while the elevator passes floor 7
  twice.
- Measured baseline (`scenarios/v1-poisson.yaml`: seed 42, 4 passengers/min for 15 min, 10 floors):
  69/69 served, wait avg **77.0 s**, p95 **193.0 s**, max **242.0 s**; ride avg 15.0 s; 248 floors travelled,
  82 stops. v2 strategies will be compared against these numbers on the same seed.
- Adding a strategy means adding an `ISchedulingPolicy`. If LOOK needs "stop for this hall call on the way",
  `ISchedulingPolicy` will grow a stop predicate, recorded in a new ADR.
- All strategies stay selectable by name (`SchedulingPolicies`, `--scheduling`).
