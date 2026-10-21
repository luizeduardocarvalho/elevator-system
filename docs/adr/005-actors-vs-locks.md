# ADR-005: Actors with one message protocol, not locks; deterministic simulation for testing

## Status

Accepted (v2-medium, 2026-10-21).

## Context

The spec asks for thread safety ("appropriate locks/mutexes", atomic state changes, no races in request
assignment), 100+ concurrent requests, and assignment in under 100 ms. It also asks for an experiment: the same seed
must give the same event log, at any speed, so strategies can be compared and runs replayed. Real threads are
non-deterministic, so one code path cannot serve both needs.

The options:
- **Locks around a shared building model.** Every elevator, the dispatcher and the callers touch the same objects
  under mutexes. That is easy to start and hard to reason about: lock ordering, holding a lock across a wait,
  dispatcher decisions made under a lock, and no way to replay a run.
- **Actors.** Each elevator, the dispatcher and the passengers are owned by exactly one loop. They communicate only
  by messages and immutable snapshots, so there is no shared mutable state to lock.

## Decision

**All decision logic lives in pure, single-threaded handlers** (`Elevator.Domain.Control`). `ElevatorHandler`,
`DispatcherHandler` and `PassengersHandler` implement `Handle(message, now, outbox)`. There is **one message
protocol** (`Messages.cs`): Assign, accept, reject, release, snapshots, boarding, and so on.

**Two shells deliver the same messages to the same handlers**, and contain no decisions.

*`SimEngine` (deterministic)* is used by `run`, `bench`, the future TUI and most tests.
- It is one priority queue keyed on (sim time, sequence number).
- The same seed gives a byte-identical log at any tick size (property-tested).

*`ElevatorSystem` (threaded, `Elevator.Runtime`)* is used by `perf`, the stress tests and a live system.
- One `Actor` per elevator: a loop over an unbounded `Channel<Message>` mailbox, sleeping until the next message or
  the elevator's next phase deadline, on a `RealClock` with a speed factor.
- A **single dispatcher** actor reading its mailbox plus a **bounded request channel**
  (`FullMode.Wait`: submitters wait when it is full, which is the backpressure).
- A passengers actor, and a single log-writer task that assigns sequence numbers.
- **No locks.** Snapshots travel as messages. The shell uses only channels, a `ConcurrentDictionary` to correlate
  acknowledgements with callers, and a `ConcurrentQueue` copy of the log for tests. None of these holds domain state.
- **The spec's `ElevatorSystem`:**
  - `assignRequest()` is `AssignRequest`, which submits and awaits acceptance;
  - `requestQueue` is the bounded channel (`RequestQueueLength`);
  - `findBestElevator()` is `FindBestElevator`: a query message to the dispatcher actor, answered from its snapshots
    without assigning;
  - `balanceLoad()` is `BalanceLoad`: the cost function's load terms always apply (ADR-006), and this also asks the
    dispatcher for an immediate rebalancing pass when rebalancing is enabled.
- **Causality, not locks, orders a caller's request.** `AssignRequest` posts `PassengerWaiting` to the passengers
  actor *before* writing to the request channel. Every later notice about that request is caused by the submission,
  so it reaches the passengers actor after the passenger exists.

**This is deterministic simulation testing.**
- Logic correctness is tested deterministically, in the sim (properties over thousands of random buildings and
  strategies).
- The threaded shell is then tested for **outcome equivalence**, not identical logs, plus thread-safety invariants.
- The next step, if needed, would be Microsoft Coyote: it controls the scheduling of the real actors, which makes
  interleaving bugs reproducible from a seed.

## Consequences

- **Thread safety by construction.** Each elevator and the dispatcher have exactly one writer. The spec's "atomic
  state changes" hold because a handler runs one message at a time to completion.
- **Barrier stress test** (`ConcurrencyStressTests`): 32 threads × 300 rounds (Nearest) and 16 × 60 (Cost),
  released at the same instant each round. It checks that:
  - every submission is assigned exactly once;
  - no request is owned by two elevators;
  - each elevator's state changes form one valid transition chain.
  It passed 10 runs out of 10.
- **The test detects races.** Starting a second loop on each elevator's mailbox (two writers) makes it fail at once
  with an actor fault ("Collection was modified").
- **Cross-check** (`CrossCheckTests`): a light 20-floor, 4-elevator scenario through both shells, with the actor
  runtime at 200× speed. Same passengers, everyone served exactly once at the right floor, invariants intact, and
  average wait and ride within timer-jitter tolerance.
- **Cost of the design.**
  - Every state change publishes a snapshot message, so there are more messages than with a shared-memory read.
  - The dispatcher works from snapshots that may be stale. That is handled explicitly by the accept/reject protocol
    (ADR-006) rather than hidden by a lock.
