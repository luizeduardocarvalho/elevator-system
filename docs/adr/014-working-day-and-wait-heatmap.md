# ADR-014: A working-day scenario and the wait heatmap

## Status

Accepted (2026-10-22).

## Context

Every scenario so far covers one traffic pattern for about 30 minutes: up-peak, down-peak or lunch. Results tables
compare strategies within one pattern, but nothing shows how a building behaves over a day. A single picture of a whole
day, telling where and when people wait, is the most direct way to show what a strategy does.

Two things were missing:
- **Traffic that changes direction during a run.** A `traffic.profile` had one origin/destination mix for the whole
  run; a day goes from mostly-up to interfloor to mostly-down.
- **A view that summarises a run by place and time**, from the event log, like every other metric (ADR-007).

## Decision

### Stepwise mixes on rate points
A rate point may carry its own `mix`, which applies from that point until the next point that has one. The rate stays
piecewise-linear; the mix is a step, which matches how a day changes (people go to lunch, then come back). The profile's
own `mix` is the default before the first point that has one.
- **Same random draws whatever the mix.** A trip always draws the same random numbers; the mix only changes the
  comparisons. Adding mixes therefore never shifts the stream, so every existing scenario is unchanged byte for byte
  (all 8 v2 results files and 4 run logs regenerated identically).
- **`clockStart: "HH:MM"`** gives the time of day at sim time 0, for labels only. It never reaches the engine or the
  log, so the event contract is untouched.

`scenarios/day.yaml` is 07:00–19:00 on the usual building (20 floors, 4 elevators, capacity 10) with the usual peak
rate (12/min): morning up-peak, lunch out then back, evening down-peak, light interfloor traffic in between.

### The heatmap (`elevator heatmap <scenario | log.jsonl>`)
The value of a cell is defined precisely:
- **What:** average wait (`--stat p95` for the 95th percentile), from call to boarding, as in the report (ADR-008).
- **Where:** the passenger's origin floor. It is where they stand waiting.
- **When:** the bin of their call time (10 minutes by default: 72 columns for a 12-hour day).
- **Nobody called:** no value (`·`), never 0 s.
- **Never boarded:** counted in the footer and kept out of every cell. A cell must not look good because its
  passengers were never picked up.
- **Margins pool passengers.** A floor's value is over all its passengers, not the mean of its cells, so a busy bin
  weighs more than a quiet one, as in the report.

**A fixed colour scale in seconds** (< 15, 15–30, 30–45, 45–60, ≥ 60 s), not one stretched to each run. The same colour
always means the same wait, so maps of different strategies or seeds compare side by side. The top level starts at
60 s, the TUI's "long wait" threshold.

**Layering.** `WaitHeatmap` (Elevator.Metrics) is computed from the log only. `HeatmapView` (Elevator.Tui.View) turns
it into lines, as Spectre markup or plain shade characters, and is pure, so it is tested without a terminal. The CLI
command only loads, runs and prints.

## Consequences

- **The day is now one experiment** ([d1-day](../results/d1-day.md), held-out seeds 21–40), wait avg / p95:

  | Strategy | Wait avg | Wait p95 | Left behind |
  |---|---|---|---|
  | FIFO + Nearest | 114.8 ± 2.4 s | 532.8 ± 7.8 s | 16.1 |
  | LOOK + Nearest | 21.0 ± 0.4 s | 70.0 ± 1.8 s | 14.3 |
  | LOOK + Cost (default) | **15.7 ± 0.1 s** | **41.5 ± 0.6 s** | 0.0 |

- **The maps show something the tables don't** ([d1-day-heatmaps](../results/d1-day-heatmaps.md)):
  - The evening down-peak and the lunch run to the lobby strand people on the upper floors. Under FIFO + Nearest they
    are solid "≥ 60 s" walls; under LOOK + Cost they are faint.
  - The morning up-peak barely shows as wait under any strategy. Elevators keep returning to the lobby, so its callers
    board quickly; up-peak costs ride time and crowding instead. A per-floor wait view is therefore not the right lens
    for up-peak; the journey and ride metrics are.
- **Limits.** With 10-minute bins, off-peak cells hold one or two passengers, so single cells are noisy; the margins
  and the hourly row are the numbers to quote. A cell reflects one seed; the paired bench is the evidence for
  differences between strategies.

## Amendment: the difference map (`--vs`)

`elevator heatmap day --vs look+nearest` draws strategy A minus strategy B on the same passengers. Two logs work too:
`heatmap a.jsonl --vs b.jsonl`.
- **Paired.** A cell groups passengers by origin floor and call time, which do not depend on the strategy, so it holds
  the same people in both runs. Its value is the mean of their paired differences (wait under A − wait under B), the
  same common-random-numbers pairing as the bench.
- **Only people who boarded in both runs** are paired; the rest are counted in the footer. Runs with different
  passengers are refused, since that would not be a paired comparison.
- **P95** is p95(A) − p95(B) per cell.
- **A fixed diverging scale**, neutral for −5 ≤ Δ < 5 s, then steps at 5, 15 and 30 s on each side. Blue means A waits
  less. Blank means nobody called, which is different from "no difference" (`~`).
- **One renderer.** `HeatmapView` draws any `IHeatGrid` with a `HeatScale`, so wait maps and difference maps share
  layout and margins.

**What it showed on the working day** (seed 1, [d1-day-heatmaps](../results/d1-day-heatmaps.md)). LOOK + Cost against
LOOK + Nearest:
- 6.2 s less per passenger overall;
- less wait in 286 cells, more in 76, within ±5 s in 481.

The largest gains are not on the top floors but on the low-middle floors (floor 3: −21 s, floor 4: −15 s), during lunch
and the evening down-peak. Under Nearest, elevators coming down from the top fill up and pass the low floors. Cost
dispatch predicts each elevator's load at pickup (ADR-006) and sends another one. This is one seed, so it is a
hypothesis; the paired bench across seeds is the evidence for the day-wide difference ([d1-day](../results/d1-day.md)).

