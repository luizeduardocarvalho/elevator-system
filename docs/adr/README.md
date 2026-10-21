# Architecture decision records

| # | Decision | Status |
|---|---|---|
| [001](001-fifo-scheduling.md) | FIFO hall-call scheduling with real mechanics | Superseded by 004 (FIFO kept as baseline) |
| [002](002-explicit-state-machine.md) | Explicit state machine with a transition table | Accepted |
| [003](003-simulated-clock-pure-core.md) | Simulated clock and a pure core | Accepted |
| [004](004-look-scheduling.md) | LOOK scheduling per elevator | Accepted |
| [005](005-actors-vs-locks.md) | Actors with one message protocol, not locks | Accepted |
| [006](006-dispatch-cost-function.md) | Cost-based dispatch, sticky assignments, staleness, aging | Accepted |
| [007](007-event-log-backbone.md) | The event log is the backbone | Accepted |
| [008](008-metrics-and-two-kinds-of-time.md) | Metric definitions and the two kinds of time | Accepted (bucket mapping amended by 013) |
| [009](009-destination-dispatch-and-capacity.md) | Destination dispatch and the capacity model | Accepted; full-load part superseded by 010 |
| [010](010-next-trip-capacity.md) | Next-trip assignment under full load | Accepted |
| 011 | TUI: choice, renderer decoupling, timestep and accumulator, seek by re-simulation | Reserved for Stage 3 |
| [012](012-eligibility-and-elevator-types.md) | Eligibility policies and elevator types | Accepted |
| [013](013-maintenance-emergency-watchdog.md) | Maintenance, emergency, watchdog and reassignment | Accepted |

The interface between Stages 3 and 4 is the [shared v3 contract](../contract-v3.md). The ADRs it touches carry an
amendment section: 002 (ElevatorMode), 003 (steppable engine), 005 (injection seam), 006 (explained decisions),
007 (v3 events) and 008 (new release reasons).
