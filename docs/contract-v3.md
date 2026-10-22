# Shared v3 contract (frozen)

The interface between **Stage 3 (TUI, branch `stage3-tui`)** and **Stage 4 (Hard, branch `stage4-hard`)**, which are
built in parallel from this commit. Both sessions build against these types and neither edits them.

**Need a change?** Stop and ask the owner. A change lands on `main` as its own commit, and both branches rebase.

**Determinism rule.** With the default configuration, everything here is inert: event logs and every bench result
are byte-identical to `v2-medium`.

## Who uses what

| Piece | Where | TUI (Stage 3) | Hard (Stage 4) |
|---|---|---|---|
| `ElevatorMode` (Normal, Draining, Maintenance, Emergency, Fault) | `Elevator.Domain` | renders mode markers | sets it |
| `ElevatorSnapshot.Mode`, `.TypeName`, `.ServedFloors` (+ `Serves(floor)`) | `ElevatorSnapshot` | renders | sets them (types, restrictions) |
| Maintenance, emergency and fault events (below) | `Events/DomainEvent.cs` | ops panel, markers, event tail | emits |
| `AccessDenied` | events | event tail | emits (eligibility pipeline) |
| `FaultCommandApplied` | events | event tail, replay | emits when a command takes effect |
| `AssignmentReason.Fault`, `.Emergency` (+ the existing `.Maintenance`) | `AssignmentReason` | event tail | releases pickups with these (`AssignmentReleased`) |
| `DispatchEvaluated` + `CandidateCost` | events | ASSIGN line (all costs + winner) | must keep filling `Candidates` if it changes the dispatch policies |
| `FaultKind`, `FaultCommand`, `IFaultInjector`, `NoOpFaultInjector`, extension helpers | `Elevator.Domain.Faults` | calls it (keys m / e / s) | implements it (sim and runtime) |
| `Scenario.FaultSchedule` + YAML `faults:` | `Scenario`, `ScenarioLoader` | passes the scenario through | acts on it (e.g. `scenarios/chaos.yaml`) |
| `SimEngine.Create / AdvanceTo / Finish / Now / Finished / Elevators / Waiting / Post / FaultInjector` | `Elevator.Simulation` | drives the live view | posts commands via `Post`, supplies the injector |
| `SimEngineOptions(EmitDispatchEvaluations, FaultInjector)` | `Elevator.Simulation` | sets `EmitDispatchEvaluations` | supplies `FaultInjector` factory |
| `ElevatorSystem.Post(Address, Message)` | `Elevator.Runtime` | — | runtime injector, outcome-equivalence test |
| `PassengersHandler.WaitingCounts` | `Domain.Control` | ▲/▼ counts (via `SimEngine.Waiting`) | — |

## Events

| Event | Fields | Meaning |
|---|---|---|
| `MaintenanceRequested` | ElevatorId | Draining starts: no new assignments, riders still delivered |
| `MaintenanceEntered` | ElevatorId, Floor | Riders delivered; parked out of service |
| `MaintenanceExited` | ElevatorId | Back in service |
| `EmergencyStopped` | ElevatorId, Floor | Emergency stop in effect |
| `EmergencyCleared` | ElevatorId | Resumed |
| `FaultDetected` | ElevatorId, Floor, SilentForMs | Watchdog: no progress for SilentForMs; calls reassigned |
| `FaultRecovered` | ElevatorId | It moved again |
| `AccessDenied` | RequestId, ElevatorId?, Policy, Reason | An access policy refused this elevator (or all, when null) |
| `FaultCommandApplied` | ElevatorId, Kind, DurationMs? | A fault or operator command took effect |
| `DispatchEvaluated` | RequestId, Candidates[{ElevatorId, Cost? (null = ineligible / ∞), Note?}], WinnerId? | Every candidate's cost for one decision. Emitted just before `AssignmentProposed`, only with `EmitDispatchEvaluations` |

The JSONL spelling follows the existing rules: enums as `MAINTENANCE_ON`, `DRAINING`; fields in camelCase.

## Fault commands

- **The record:** `FaultCommand(At, ElevatorId, Kind, DurationMs?)`, where Kind ∈ MaintenanceOn, MaintenanceOff,
  EmergencyStop, EmergencyClear, Stuck, Unstick. `DurationMs` applies to Stuck only; null means until Unstick.
- **Timing:** `IFaultInjector.Inject(command)` takes effect at max(command.At, now), in **sim time**, so runs with
  injected commands stay deterministic and can be replayed.
- **The no-op:** `NoOpFaultInjector.Instance` ignores everything and has `IsNoOp = true`; the TUI shows "(no-op)".
- **Scenario YAML:**

  ```yaml
  faults:
    - { atS: 300, elevator: 2, kind: maintenance-on }
    - { atS: 600, elevator: 2, kind: maintenance-off }
    - { atS: 420, elevator: 1, kind: emergency-stop }
    - { atS: 450, elevator: 1, kind: emergency-clear }
    - { atS: 800, elevator: 3, kind: stuck, durationS: 120 }
  ```

  The loader validates the elevator index, the kind, the time, and that durationS is used only with stuck. It sorts
  the commands by time. In this commit the engine does not act on them; that is Stage 4's job.

## Steppable engine

- **Same log.** `SimEngine.Run` is `Create` + `AdvanceTo` in fixed ticks + `Finish`. `AdvanceTo` in *any* step sizes
  gives the same log (property-tested), so the TUI's fixed tick and speed setting cannot change results.
- **Read-only views.** `Elevators` (snapshots as of each elevator's last handled message) and `Waiting` are for
  rendering only. The renderer must not mutate the engine.
- **Commands.** `Post(to, message)` delivers at `Now`, after anything already due at `Now`. Commands enter the engine
  only this way (or through an `IFaultInjector` built on it).

## Byte-identity target (both stages)

With faults off and the default configuration, the following must regenerate byte-identically (ignoring the Runs and
Command lines):
- `docs/results/m5-next-trip.md` and `docs/results/m5-capacity-modes.md`;
- `m1`–`m4` as regenerated at this commit. Their headers explain why the committed copies differ slightly, by commit
  `d5864a0`.

## ADR numbers

001–010 exist. **011 = TUI** (Stage 3). **012 = eligibility policies and elevator types**, **013 = maintenance,
emergency, watchdog and reassignment** (Stage 4).

## v3.1 amendment: passenger attributes and fleet in the log

Added after both stages were built, to close three gaps they reported:
- the TUI showed load in people, not units;
- `bypassed_full` used the building-wide capacity on typed fleets;
- VIP waits could not be measured.

All fields are **optional and written only when they carry information**, so default runs serialise exactly as
before and logs written before the amendment read the same (tested with v2-medium lines).

| Event | New field | Meaning when absent | Written when | Writer | Readers |
|---|---|---|---|---|---|
| `PassengerArrived` | `Load` (int?) | 1 unit (a person) | ≠ 1 | Stage 4 passengers handler | TUI (load units, joined by passenger id), Metrics |
| `PassengerArrived` | `Vip` (bool?) | not VIP | true | Stage 4 passengers handler | Metrics (VIP vs non-VIP wait), TUI |
| `PassengerArrived` | `AccessLevel` (int?) | 0 | > 0 | Stage 4 passengers handler | event tail, audits |
| `SimulationStarted` | `Fleet` (list of `FleetEntry(ElevatorId, Type, Capacity, BypassThreshold, ServedFloors)`) | every elevator uses the building values | a typed fleet is configured | Stage 4 engine setup | Metrics (per-elevator full threshold for `bypassed_full`), TUI (type, capacity in units) |

- **Helpers:** `PassengerArrived.LoadUnits` and `.IsVip` (never serialised).
- **No per-boarding fields.** `PassengerBoarded` and `PassengerAlighted` are unchanged: consumers join the arrival by
  passenger id.

## Amendment: the final design (ADR-018)

The contract is unchanged. Two of its values became constants: `SimulationStarted.scheduling` is always `look` and
`SimulationStarted.capacityMode` is always `next-trip`. The fields stay so that older logs keep their meaning and
replay unchanged. The release reasons `Rebalance` and `BypassedFull` are gone. Only runs with rebalancing or capacity
mode `defer` ever emitted them; read those logs at tag `v3-full-grid`. (The TUI's tolerant reader skips such lines.)
