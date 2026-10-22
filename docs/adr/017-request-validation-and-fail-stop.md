# ADR-017: Request validation at the runtime boundary and fail-stop supervision

## Status

Accepted (2026-10-22). Amends ADR-005 (actor runtime).

## Context

The spec asks the system to "handle invalid floor requests gracefully" and to "add proper exception handling for
concurrent operations". Two gaps in the actor runtime broke both:

- **`ElevatorSystem.AssignRequest` did not validate.** `Request.Create` checks floors, direction and load, but `Request`
  is a public record, so `new Request(99, 5, …)` skipped the check. The only graceful rejection (`RequestRejected`)
  lived in the Easy-stage `ElevatorController`, which no runtime path calls.
- **Actor loops had no supervision.** An exception in a handler ended that actor's task silently. Callers waiting on
  it never completed, and the actors that depended on it stopped making progress.

A probe confirmed both on a 20-floor system with 3 elevators: submitting `new Request(99, 5, Down)` threw an
`IndexOutOfRangeException` inside an actor. That request never completed, and a valid request submitted after it also
hung (both timed out after 3 s). One bad input stopped the whole building.

## Decision

**Validate at the boundary.** `Request.Check(floors)` holds the one set of rules: both floors in range, pickup ≠
destination, direction consistent with the floors, load > 0. `Request.Create` uses it, and so do `AssignRequest` and
`FindBestElevator` before anything reaches an actor:

- `AssignRequest` logs a `RequestRejected` event (kind `Request`) and throws `ArgumentException`. The system keeps
  serving.
- `FindBestElevator` throws `ArgumentException` without logging, because a query changes nothing.

**Fail-stop supervision.** Every actor loop, and the delayed-delivery loop, runs under `Supervise`. The first
exception:

1. becomes `ElevatorSystem.Fault`;
2. fails every pending `AssignRequest` and `FindBestElevator` with `ElevatorSystemFaultedException`, whose inner
   exception is the handler's;
3. closes the request queue, so submitters waiting for room are released;
4. stops the other actors.

Later calls fail at once. `DisposeAsync` does not rethrow a fault that was already reported.

**Why stop rather than restart the actor.** A handler that threw may have left its state half-updated: an elevator
with a rider and no stop, or a dispatcher with an assignment that has no acknowledgement. The other actors also wait
on its replies. Restarting it from that state would serve requests from a building that no longer matches its own
log. A handler exception is a bug, not an operating condition: elevator faults are modelled explicitly (ADR-013) and
never throw. Fail-stop turns the bug into a loud, immediate failure instead of a silent hang.

**Race between a caller and a fault.** `AssignRequest` checks for a fault, registers the call, then checks again.
`OnFault` sets the fault before sweeping the pending calls. Both sides use full fences, so either the sweep sees the
registration or the second check sees the fault. No call is left waiting.

## Consequences

- An invalid request costs its caller an exception, and costs the system nothing: the probe scenario is now a test
  (`SupervisionTests`), with six kinds of invalid request each followed by a valid one that is assigned normally.
- A handler bug fails every caller fast, with the cause attached. `SupervisionTests` injects one through a handler
  that throws on a chosen request.
- The runtime no longer depends on `ElevatorController` for graceful rejection, so ADR-018 can remove it.
- The simulator is unchanged: scenarios are validated when loaded, and a handler bug there throws straight to the
  test or command that ran it. The golden event logs of `lunch.yaml` and `chaos.yaml` are byte-identical.
- The hot path gains two volatile reads per call. The perf gate (`PerfGateTests`) still passes.
