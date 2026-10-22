# ADR-015: Ordering rules

## Status

Accepted (2026-10-22). Supersedes the VIP-priority part of ADR-012. Amends ADR-006 (re-dispatch order) and ADR-013
(the emergency lane).

## Context

Order decides correctness in several places in this system: which message an actor handles first, which request
the dispatcher serves next, who boards first, what the log says happened first. The pieces were decided one at a
time (ADR-005, 006, 012, 013) and never stated as one rule. An audit of every queue, channel, mailbox, priority queue
and order-sensitive iteration found that most of them were right, and it also found eight places where order was
wrong or unspecified:
- **Two in both shells:**
  - an elevator handing pickups back before the dispatcher had its explaining snapshot;
  - an emergency clear overtaken by a later stop.
- **Four in the actor runtime only:**
  - delayed messages with no order at all;
  - an older snapshot replacing a newer one;
  - `AssignRequest` logging a request before its passenger;
  - fault commands applied in any order.
- **Two where queue position stood in for something the cost function or the call time should decide:**
  - VIPs reordering the pending set;
  - hand-backs re-dispatched in stop order instead of call order.

## Decision

**The rule:**

> FIFO where order is a correctness property, priority where urgency differs, fairness decided by the cost function,
> not queue position.

Applied:

| Where | Order | Why |
|---|---|---|
| Every actor mailbox (single reader, many writers) | FIFO per sender | A sender's messages are causally ordered (accept before release, snapshot before release) |
| Link latency in the runtime (`DelayedDelivery`) | (due sim time, enqueue seq) | Equal delays from one sender keep their order; a delayed snapshot can't arrive after the release it explains |
| Emergency lane (runtime `Control` channel; sim priority 0) | Ahead of everything; FIFO between stop and clear | Urgency: an emergency must not wait behind traffic. Stop and clear keep their order among themselves |
| Dispatcher lanes (runtime): mailbox before new requests | Control traffic (snapshots, acks, releases, queries), then new submissions; FIFO within each | Urgency: the dispatcher updates its view before taking new work. This is the second and last priority rule; pinned by a test |
| Simulator queue | (sim time, priority, seq); priority 0 only for emergency stop and clear | Determinism: same seed, same log, at any tick size |
| Snapshot before release (`ElevatorController.SendRelease`) | The snapshot is published first and the release travels with the same link delay | Causality: the dispatcher must see why a pickup came back before reassigning it, or it proposes it straight back |
| Newest snapshot (`DispatcherHandler`) | An older version never replaces a newer one | Two delayed snapshots can cross in the runtime |
| Pending requests (`DispatcherHandler._pending`) | (call time, id), served oldest first on every snapshot | Aging: whoever called first is served first. **Nothing reorders it**: urgency is not a queue position |
| Hand-back batch (`Elevator.ReleaseAllPickups`) | (call time, id) | Re-dispatch keeps each passenger's place by call time, not by where the stop sat in one elevator's list |
| VIP priority (`vip.priority`) | **A cost term**: a VIP request is not charged λ·ΔD, the delay it adds to others, so it takes the fastest pickup | Fairness and urgency through the cost function and eligibility (VIP-dedicated elevators), never by moving a request up a queue |
| Boarding (`PassengersHandler.GetOn`) | Arrival order per floor and direction; one door opening is one batch; strict no-overtaking | Fairness at the door. No VIP boarding exception |
| Dispatch tie-breaks | The lowest elevator id, explicitly, whatever the candidate order | Determinism and explainability |
| `AssignRequest` (runtime) | The caller waits for room in the bounded queue, then the passengers actor logs `PassengerArrived` and submits, in one handler call | Causality by construction: nothing about a request is logged before its passenger. Backpressure stays with the callers |
| Runtime fault commands | Due now: straight onto the lane. Future: (At, injection order) through `DelayedDelivery` | As the simulator's fault scheduler. Edge: a future command whose time has just come but not yet been delivered can be overtaken by one injected for "now"; both belong to the same instant, and the later one is the operator's latest intent |
| Event log | One writer assigns seq; per actor, events are recorded before that handler call's messages are delivered | Causal within an actor and along messages; across actors in the runtime, the scheduling interleaving (ADR-007) |

**FIFO the scheduling baseline** (ADR-001) serves an elevator's stops in the order they were registered on *that*
elevator. A re-dispatched request joins the back of its new elevator's list. That is the baseline's definition, kept
as measured, not a fairness rule.

## Consequences

**Changes, each with its test (written first, seen failing) and its measured effect:**

| Commit | Change | Test | Effect on results |
|---|---|---|---|
| `536205b` | Snapshot before every release; releases share the link delay | `ReleaseOrderTests` (rebalance, Defer bypass, maintenance, emergency, fault; at 0 and 1.5 s latency) | Default system byte-identical. Defer-mode and rebalance runs move within their CIs: m1 baseline up-peak 25.9 → 26.1 s; rebalancing at down-peak −0.9 → −0.8 ± 0.5 s, and at lunch no longer significant |
| `67579f4` | Dispatcher keeps the newest snapshot | `SnapshotVersionTests` | None (the simulator delivers in order) |
| `efbf8d8` | Ordered delayed delivery in the runtime | `MessageOrderTests` (16 concurrent senders; equal delays; due-time order) | Runtime only |
| `973b1e1` | `AssignRequest` logs the passenger first, by construction; `PassengerWaiting` removed | `LogCausalityTests` (277 and 100 of 800 requests out of order before, 0 after); `LogCausalityProperties` (sim) | Runtime only. p99 at load avg 7.9: 100 → 12.2 → 14.2 ms, 1k → 53.6 → 54.4 ms, 10k → 201.9 → 216.7 ms; SLOs pass. To be re-measured on a quiet machine |
| `e320b64` | Hand-back batches oldest call first | `RedispatchOrderTests` | Defer-mode only, within 0.1 s on averages |
| `a0515ec` | VIP priority is a cost term | `CostBasedDispatchTests.Vip_priority_...`; `DispatcherAccessTests.Pending_requests_are_served_by_call_time_...` | fleet.yaml has one scripted VIP: h2 p95 39.8 → 39.7 s. Measuring the term needs VIP-share traffic (follow-up) |
| `33a4ea6` | Emergency stop and clear share the lane | `EmergencyPreemptionTests.A_clear_posted_before_a_stop...`, `...Lanes_are_emergency_then_mailbox_then_new_requests_each_fifo`; `EmergencyScenarioTests.A_clear_posted_before_a_stop_at_the_same_instant...` | None |
| `403d8a1` | Runtime fault commands in (At, injection order) | `FaultInjectorOrderTests` | Runtime only |
| `cfbaade` | Golden logs: lunch (LOOK + Cost, 4 elevators) and chaos | `DeterministicScenarioTests.Golden_event_log_hash` | The lunch golden equals the v2-medium log byte for byte |

**Deviation from the audit's plan (fix 5).** The plan proposed sending every rejected or released request back
through the ordered retry. With the snapshot-before-release rule, the dispatcher already serves every waiting request
oldest-first before it handles a release. Retrying on every message would add one dispatch decision per pending
request per message, which is quadratic under overload, exactly where the runtime is stressed. The order problem that
remained was inside a hand-back batch, and that is fixed at the source.

**Mutation checks.** For each guarantee, the smallest deliberate break was applied, the named tests run, and the
source restored:

| Guarantee | Break | Caught by |
|---|---|---|
| Snapshot before release | Publish after sending the release | `ReleaseOrderTests` (4) |
| Release travels with the snapshot delay | Send the release with no delay | `ReleaseOrderTests` (5) |
| Newest snapshot wins | Accept any version | `SnapshotVersionTests` |
| Delayed delivery FIFO | Sequence reversed among equal due times | `MessageOrderTests` |
| Delayed delivery FIFO | The old one-Task-per-message path | `MessageOrderTests` |
| Sim ties by insertion order | `-seq` (LIFO among ties) | `Golden_event_log_hash` (both) |
| Emergency priority (sim) | Everything priority 1 | `EmergencyScenarioTests` |
| Emergency lane (runtime) | Stop to the mailbox | `EmergencyPreemptionTests` |
| Only emergency commands overtake | Maintenance on the lane | `Other_commands_queue_behind_the_mailbox` |
| Stop and clear share the lane | Clear not on the lane | `A_clear_posted_before_a_stop...` (sim and runtime) |
| Mailbox before new requests | Requests read first | `Lanes_are_emergency_then_mailbox_then_new_requests_each_fifo` |
| Pending oldest first | Reversed retry | `Pending_requests_are_served_by_call_time...` |
| Hand-back batch order | Stop order | `A_hand_back_batch_is_emitted_oldest_call_first...` |
| Aging never overrides capacity or out-of-service | Aging check first | `Capacity_and_maintenance_rejects_always_win` |
| Capacity check | Removed | `Capacity_and_maintenance_rejects_always_win` |
| Boarding in arrival order | Candidates reversed | `HeavyLoadTests...strict_arrival_order...`, `System_invariants_hold` |
| Nearest ties to the lowest id | Descending | `Ties_go_to_the_lowest_id` |
| Cost ties to the lowest id | Descending | `Exact_cost_ties_go_to_the_lowest_id...` |
| VIP never reorders pending | VIPs first | `Pending_requests_are_served_by_call_time...` |

All 19 are caught. Two needed attention:
- **"Cost ties"** was missed until its test was added. That gap was the one the audit predicted.
- **"Aging"** first ran as an equivalent mutant: the inserted check sat below the capacity check, so it changed nothing.

**Still open:**
- **P3 hygiene from the audit:**
  - make order explicit for the refusal reason, the registries' names, `BenchAnalysis` input, and `Stats()`;
  - `demo.yaml` still uses FIFO;
  - runtime-log consumers (sort by (t, seq); check seq; `SimulationStarted` in runtime logs);
  - the shuffle-determinism tests (T6a/b);
  - the capacity-block boarding property.
- **Perf after fix 4.** `docs/results/perf.md` and the curves predate fix 4. They must be re-measured on a quiet machine.
- **A known flake.** The chaos cross-check (outcomes within wall-clock tolerance) failed once in 20 full runtime-suite runs after fix 8, against 0 in 6 before. It passed 16 times out of 16 on its own. No cause is claimed.
