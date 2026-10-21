# ADR-002: Explicit state machine with a transition table

## Status

Accepted (v1-easy, 2026-10-21)

## Context

An elevator is safety-critical behaviour expressed as states: it must never move with the doors open. The spec
gives the states: `IDLE, MOVING_UP, MOVING_DOWN, DOOR_OPENING, DOOR_OPEN, DOOR_CLOSING, MAINTENANCE`. State
changes spread across `if`s in many methods are hard to review and impossible to enumerate in tests.

## Decision

- `TransitionTable` is a single dictionary `(state, trigger) → state` and the only way `Elevator` changes state.
  Any pair that is not in it throws `IllegalTransitionException`, which signals a programming error. Invalid user
  input never reaches it: `ElevatorController` rejects bad input first, with a `RequestRejected` event.
- Triggers: `StartMoveUp, StartMoveDown, ArriveAndOpen, DoorsFullyOpen, StartClosing, DoorsFullyClosed, Reopen,
  EnterMaintenance, ExitMaintenance`.

  | From | Trigger | To |
  |---|---|---|
  | Idle | StartMoveUp / StartMoveDown | MovingUp / MovingDown |
  | Idle, MovingUp, MovingDown | ArriveAndOpen | DoorOpening |
  | DoorOpening | DoorsFullyOpen | DoorOpen |
  | DoorOpen | StartClosing | DoorClosing |
  | DoorClosing | DoorsFullyClosed | Idle |
  | DoorClosing | Reopen | DoorOpening |
  | Idle / Maintenance | EnterMaintenance / ExitMaintenance | Maintenance / Idle |

- The safety invariant is structural: the only edges into a moving state leave from `Idle`, and `Idle` is reached
  only with the doors fully closed. There is no edge from a door state to a moving state.
- **Full door cycle.** The full spec door cycle (opening, open, closing) is modelled, including reopening when a
  matching hall call arrives while the doors are closing. The doors reverse from where they are, so a reopen takes
  `closedFor × opening/closing` ms.
- **Maintenance.** The `Maintenance` edges exist and are tested, but nothing uses them until v3 (maintenance
  mode). Calls registered during maintenance are held.
- **Enums.** C# enums keep the spec's members and order, in PascalCase (`MovingUp`). The event log and CLI use
  the spec spelling (`MOVING_UP`) through `JsonStringEnumConverter(SnakeCaseUpper)`.
- **No direction.** "No direction" (lantern off) is `Direction?`, not an extra enum member.
- **Spec method names.** The spec's `moveUp()`, `moveDown()`, `openDoor()`, `closeDoor()` and `addRequest()` exist
  on `Elevator` and go through the table. `Tick` drives them in normal operation.

## Consequences

- Tests state the machine independently: `TransitionTableTests` lists the legal edges by hand and checks that
  every other (state, trigger) pair throws (all 7×9 = 63 combinations).
- The FsCheck property `Safety_invariants_hold_for_any_call_sequence` replays random call/time sequences and checks
  that every `StateChanged` is a table edge, and that the floor changes only while moving, by one floor, within
  1..10. A mutation check (moving two floors per step, or forgetting to clear a car call) is caught at once.
- A new behaviour such as an emergency stop means a new trigger plus new rows in one place, recorded in an ADR.
