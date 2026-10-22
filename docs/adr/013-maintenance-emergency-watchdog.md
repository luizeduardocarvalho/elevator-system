# ADR-013: Maintenance, emergency stops, watchdog and reassignment

## Status

Accepted (Stage 4, Hard; branch `stage4-hard`, 2026-10-21). Builds on ADR-002 (transition table), ADR-003 (sim time),
ADR-005 (actors, one protocol), ADR-008 (wait buckets) and the shared v3 contract (`ElevatorMode`, `IFaultInjector`,
`FaultCommand`, the v3 events, `SimEngine.Post`, `ElevatorSystem.Post`). Amends ADR-008 (bucket mapping, below).

## Context

The Hard level asks for maintenance mode, emergency stops and "timeouts for stuck elevators". These take an elevator
out of service while it may hold riders and assigned pickups. Every one of those people must still be served, and
nothing new may be given to it. The constraints:
- **The spec's states are frozen.** `ElevatorState` has no Emergency or Fault. The contract added `ElevatorMode`
  (Normal, Draining, Maintenance, Emergency, Fault) next to it.
- **Both shells must behave the same:** the deterministic engine (sim time, one queue) and the actor runtime (real
  threads, mailboxes).
- **An emergency must not wait behind a queue of traffic** in either shell.
- **A fault-free default run must not change** by a single byte, even though the watchdog is on by default.

## Decision

### Modes, not states
`Elevator.Mode` is derived from flags owned by the elevator, in priority order: Emergency > Maintenance (parked) >
Fault > Draining > Normal. **Only a Normal elevator takes assignments.**
- *Elevator side:* it rejects with reason Maintenance (draining or parked), Emergency or Fault. This wins over
  everything, forced assignments included.
- *Dispatcher side:* an elevator whose snapshot is not Normal is never a candidate.

The spec's states are reused: a halted or parked elevator is `Idle` or `Maintenance`. New physical transitions go
through the table (ADR-002), with table-driven tests:

| From | Trigger | To | Why |
|---|---|---|---|
| MovingUp / MovingDown | `EmergencyHalt` | Idle | stop level with the next floor, doors closed |
| DoorOpening / DoorOpen | `EmergencyClose` | DoorClosing | doors close (reversing if still opening) and stay closed |

Moving states are still entered only from Idle, so "never moves unless the doors are closed" stays structural.

### Commands: one message, two injectors
- **The message.** A `FaultCommand` reaches its elevator as an `ApplyFault` message, handled by its single owner (no
  lock, no shared flag). A command that changes something is logged as `FaultCommandApplied`, before what it causes.
  A no-op command (e.g. emergency stop while parked) is not logged.
- **Simulator.** `SimFaultInjector` posts to an internal `FaultScheduler` handler. That handler re-sends the command to
  its elevator with a delay of max(0, At − now), so the engine's queue does the waiting. The scenario's `faults:`
  schedule is enqueued at `Create`. Everything enters in sim time: same seed, same log, at any tick size (a property
  with faults checks it).
- **Actor runtime.** `RuntimeFaultInjector` posts at once, or after waiting on the system clock until At.
  `ElevatorSystem.FaultInjector` exposes it.
- **Ordering.** The elevator publishes its new snapshot before the pickups it hands back. The dispatcher therefore
  already sees it out of service when it reassigns them, and does not propose them straight back.

### Maintenance draining
- **On request.** `MaintenanceRequested`, mode Draining. Every pending pickup is handed back at once
  (`AssignmentReleased` reason Maintenance), so the dispatcher reassigns them now rather than after the riders are
  delivered. Riders are still delivered.
- **Parking.** When idle and empty, it parks where it is through the existing `Idle → Maintenance` edge
  (`MaintenanceEntered`).
- **Exit.** `MaintenanceOff` leaves the parked state, or cancels a drain still in progress (`MaintenanceExited`).
- **End of run.** A parked elevator no longer keeps a finished run alive.

### Emergency stop
- **When it takes effect.** At once: `EmergencyStopped(floor)`, and pickups are handed back (reason Emergency).
- **The next safe point.**
  - *Moving:* it finishes the floor segment it is in (it cannot stop between floors) and halts at the next floor with
    the doors closed.
  - *Doors open:* they close now. *Doors opening:* they reverse, taking as long as they had opened. *Doors closing:*
    they finish closing.
  - *Idle:* it stays.
- **While stopped.** The doors never open, so riders stay inside, protected. No new phase starts.
- **Clear.** `ClearEmergency` resumes normal decisions. If riders are headed to the floor where it stopped, it opens
  there first. If maintenance was requested, it drains again. An emergency that ends while the car is still moving just
  carries on.
- **Preemption.**
  - *Actor runtime:* each elevator actor has a **priority control channel**, read before its mailbox. `ElevatorSystem.Post`
    routes emergency stops there and everything else to the mailbox.
  - *Simulator:* the queue key is (time, priority, sequence), and emergency stops (and their scheduler hop) get
    priority 0, so they take effect before messages already due at the same instant. Every other message keeps the
    (time, sequence) order, so `Post` still delivers after what is already due.
  - *Tests:* 50 assignments are queued for an elevator, then the emergency is posted. Both shells apply the emergency
    first and reject all 50 with reason Emergency. Both tests fail if the priority is removed. Other commands queue
    normally.

### Stuck drives and the watchdog
- **`Stuck` freezes the drive.**
  - The current timed phase stops progressing, and so does any phase started meanwhile. On `Unstick` (or after
    `DurationMs`) it resumes with the time it had left.
  - The controller keeps working. The snapshot shows no phase end, so a cost projection of a stuck elevator never
    reaches its pickups.
- **The watchdog lives in the dispatcher,** on the snapshot stream it already receives.
  - *Watched:* an elevator in Normal or Draining mode whose snapshot shows work (moving, doors in a cycle, or stops
    pending).
  - *Progress:* a change of floor, state or phase end. Passing a floor without stopping publishes no snapshot, so the
    elevator also sends a small `ProgressReported` per floor reached. Only the watchdog reads it; it never triggers a
    dispatch decision.
  - *Timer:* one at a time, for the earliest deadline, and only while something is watched.
- **Detection.** No progress for **T** → the dispatcher sends `FaultDeclared(silentFor)` and stops dispatching to
  that elevator until it answers. The elevator logs `FaultDetected` (its own event, so its mode can be followed from
  its own log) and goes into Fault mode. It hands its pickups back (reason Fault) and keeps its riders.
- **Recovery.** `FaultRecovered` as soon as a timed phase completes, or at once if it is unstuck with nothing left to
  do. The dispatcher never has to clear a fault itself, and an elevator can never be excluded forever.
- **T = 30 s of sim time by default** (`watchdog.timeoutS`).

### Metrics (from the log only)
- **`OpsMetrics`:**
  - per elevator: maintenance time, of which draining; emergency stops and their duration; stuck time; faults;
  - fault timing: time to detect (stuck → `FaultDetected`, or the reported silence) and to recover
    (`FaultDetected` → `FaultRecovered`);
  - pickups handed back because an elevator went out of service, and the passengers affected;
  - access denials and refusals.
- **Wait penalty of reassignments:** `reassignment` + `blocked` bucket seconds.
- **Where they show:** `run` prints an Operations section, and `bench` an Operations table, only when something
  operational happened. Fault-free output is unchanged.
- **Comparisons:** `bench --faults on,off` pairs a scenario with and without its schedule on the same passengers.

**Amendment to ADR-008 (bucket mapping).** The `blocked` bucket also covers:
- time assigned to a **stuck** elevator (intervals are split at a timed stuck's end, so buckets still sum exactly);
- time after a **rejection for emergency or fault** (as maintenance rejections already did).

Releases for maintenance, emergency or fault stay `reassignment`, as the contract amendment said.

## Consequences

**Byte identity holds with the watchdog on.**
- All eight bench results files and the four default logs regenerate identically after every milestone.
- A property asserts that no fault is ever detected on fault-free random buildings, with snapshot latency up to 6 s.
- The first watchdog draft did fire on a fault-free run: a 15-floor trip without stopping published no snapshot for
  30 s. That is why progress reports exist.

**What chaos costs** — [h1-chaos](../results/h1-chaos.md), held-out seeds 21–40.
- *The schedule* ([scenarios/chaos.yaml](../../scenarios/chaos.yaml)): the lunch profile plus 10 minutes of
  maintenance, two emergency stops (60 s and 30 s), a 120 s stuck and a 180 s stuck.
- *The deltas* are paired against the same passengers without faults:

| Strategy | Wait avg (chaos / none) | Δ wait avg | Δ p95 | Pickups handed back | Penalty (s per run) |
|---|---|---|---|---|---|
| LOOK + Cost | 19.6 / 13.8 | +5.8 ± 0.8 | +20.5 ± 3.3 | 5.2 | 68.5 |
| LOOK + Nearest | 25.6 / 21.0 | +4.6 (+11.8 vs Cost baseline) | +8.9 | 10.6 | 134.4 |

- *Everyone is still delivered* (100 % served), and every fault is detected and recovered.
- *Cost dispatch* hands back half as many pickups as Nearest, because it rarely assigns to an elevator whose projection
  shows it stuck. It therefore pays half the penalty.

**Choosing T** — [h3-watchdog-timeout](../results/h3-watchdog-timeout.md) and
[h4-watchdog-false-positives](../results/h4-watchdog-false-positives.md), from `scripts/watchdog-sweep.sh`:

| T | Time to detect | Penalty (s per run) | Δ wait avg vs no faults | False faults per run, fault-free typed fleet | … up-peak / down-peak |
|---|---|---|---|---|---|
| 5 s | 6.0 s | 9.5 | +5.8 ± 0.7 | **≈ 61–65** | 0 |
| 10 s | 11.3 s | 21.0 | +5.9 ± 0.8 | 0 | 0 |
| 30 s | 31.9 s | 68.5 | +5.8 ± 0.8 | 0 | 0 |
| 60 s | 61.2 s | 129.0 | +6.0 ± 0.8 | 0 | 0 |

- *Detection time tracks T* (≈ T, slightly more when the elevator got stuck before it had work).
- *The penalty grows linearly with T,* but the average wait barely moves: cost dispatch already steers new requests
  away from a stuck elevator, whose projection never reaches them. The watchdog only rescues pickups assigned before
  the stall.
- *The lower bound on T is the longest legitimate phase.* The freight type's 6 s door dwell trips a 5 s watchdog about
  60 times a run. 10 s is clean on every profile here.
- *Why 30 s, not 10 s:* 30 s is five times the longest phase in any shipped type. It leaves room for slower types,
  doors held open, or latency that a 10 s timeout would not, at about 24 s of extra blocked waiting per stuck episode
  (47.5 s per run, for its two). It is a
  setting, so a building with only fast cars can lower it.

**Equivalence across shells.** A light chaos scenario (maintenance, emergency, timed stuck) runs through the sim
engine and through real actors with the runtime injector. In both:
- everyone is delivered, and the same commands are applied;
- the same maintenance and emergency episodes happen, and every fault recovers;
- nothing is accepted while out of service;
- waits are within jitter.

**Invariants over random buildings, types, access policies, loads and fault schedules:**
- no assignment is accepted while Draining, Maintenance, Emergency or Fault;
- no request is lost;
- every rider is delivered by the elevator they boarded;
- doors never open in an emergency or at an unserved floor;
- the buckets sum exactly.

**Stuck detection uses the snapshot's missing phase end.** That is a simplification: a real controller would infer a
stall from expected arrival times. The watchdog itself does not use it; it only looks at progress.

**Contract.** None of the frozen contract changed.
- New messages (`ApplyFault`, `FaultDeclared`, `ProgressReported`, `WatchdogTimer`, `RequestRefused`, `ScheduleFault`),
  the `ActorKind.FaultScheduler` address, options with defaults and the queue's priority key are internals.
- `SimEngineOptions.FaultInjector` still defaults to the no-op injector. The TUI must pass `SimFaultInjector.Factory`
  for its m / e / s keys to act.

## Amendment: the emergency lane (ADR-015)

Emergency stop **and clear** take the priority lane in both shells (runtime `Control` channel; sim priority 0), FIFO
between themselves, so a clear posted before a stop stays before it. Before, only the stop jumped the queue: a clear
posted just ahead of a stop was applied after it and left the elevator running. Every release now follows the
snapshot that explains it on every path, including with link latency, and runtime fault commands apply in
(At, injection order).

