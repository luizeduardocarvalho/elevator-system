# ADR-007: The event log is the backbone

## Status

Accepted (v2-medium, 2026-10-21). Formalises what v1 started (ADR-003).

## Context

Several consumers need to know what happened in a run:
- the report and the bench (metrics);
- the per-passenger CSV and the wait attribution;
- property tests and invariant checks;
- the future TUI and `replay`;
- perf's ownership checker.

If each consumer called into the elevators, the domain would grow hooks for every one of them, metrics would depend
on internal state, and two runs could not be compared after the fact.

## Decision

- **The domain produces events and nothing else observes it.**
  - Handlers return immutable `DomainEvent` records (`Elevator.Domain.Events`), stamped with sim time.
  - The domain never logs, prints or calls a consumer.
- **One log, in order.** The shells append events in delivery order. The sim engine's queue order is deterministic.
  In the actor runtime a single log-writer task assigns sequence numbers.
- **JSONL on disk.** One object per line, `{"seq":n,"type":"…","t":ms,…}`, with enums in the spec's spelling
  (`MOVING_UP`). Written by `JsonlEventLog`; every event type round-trips (tested).
- **Consumers read events only.**
  - `ReportBuilder` and `PassengerLifecycle` (wait attribution) take an event sequence.
  - `run` builds its report by reading back the file it just wrote, proving the log alone is enough.
  - `bench` feeds the same records in memory.
  - The perf ownership checker is a log sink.
- **What is logged** covers the whole story:
  - elevator state changes, floors, doors;
  - the dispatch protocol (submitted, proposed, accepted, rejected, released, deferred, duplicate, overload mode);
  - passengers (arrived, boarded, alighted, left behind);
  - simulation start (configuration) and end.
- **Schema evolution** is by trailing optional fields with defaults (e.g. `SimulationStarted.CapacityMode`), so older
  logs still read.

## Consequences

- **Reproducibility is testable.**
  - The same seed gives a byte-identical log at any tick size: property-tested over random buildings and strategies.
  - Strategies compare on identical passengers because the log records them.
- **Every number traces back.** Every number in a results file can be traced to events in a log. The wait buckets in
  particular are recomputed from the log, not instrumented in the domain.
- **Safety properties are checked on the log.** Single ownership of a request, load ≤ capacity, boarding order and
  exactly-once service are all checked on the event stream, in the sim and in the actor runtime alike.
- **Cost: volume.** A 30-minute peak produces ~10k events. Long soaks therefore stream to a sink
  (`RuntimeOptions.KeepEvents = false`) instead of keeping the log in memory. The heap stays flat (perf soak).
- **Order in the actor runtime is per arrival at the writer.** Events from different actors can be a few ms out of
  sim-time order, so consumers that need chronology sort by `t` (stable) first. The cross-check test does this.
