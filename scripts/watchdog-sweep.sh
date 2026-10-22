#!/usr/bin/env bash
# Watchdog timeout T (ADR-013): the same scenarios with T overridden, written as scenario variants to out/watchdog/.
#   docs/results/h3-watchdog-timeout.md          detection time and wait penalty vs T, on the chaos schedule;
#   docs/results/h4-watchdog-false-positives.md  faults declared on fault-free runs vs T (any is a false positive).
# Usage: scripts/watchdog-sweep.sh
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build -c Release -v q src/Elevator.Cli >/dev/null
E=src/Elevator.Cli/bin/Release/net10.0/elevator
DIR=out/watchdog
mkdir -p "$DIR"

variant() { # scenario T: scenarios/<scenario>.yaml with watchdog.timeoutS = T, named <scenario>-t<T>
  grep -v '^watchdog:' "scenarios/$1.yaml" | sed -e "s/^name: $1\$/name: $1-t$2/" > "$DIR/$1-t$2.yaml"
  echo "watchdog: { timeoutS: $2 }" >> "$DIR/$1-t$2.yaml"
}

for t in 5 10 30 60; do variant chaos "$t"; done
for s in up-peak down-peak fleet; do for t in 5 10 30; do variant "$s" "$t"; done; done

"$E" bench --scenarios "$DIR/chaos-t5.yaml,$DIR/chaos-t10.yaml,$DIR/chaos-t30.yaml,$DIR/chaos-t60.yaml" \
  --strategies cost --faults on,off --baseline cost/no-faults --seeds 20 --first-seed 21 \
  --out docs/results/h3-watchdog-timeout.md --title "H3: watchdog timeout T on the chaos schedule (held-out seeds 21-40)" >/dev/null

"$E" bench --scenarios "$(for s in up-peak down-peak fleet; do for t in 5 10 30; do printf '%s,' "$DIR/$s-t$t.yaml"; done; done | sed 's/,$//')" \
  --strategies nearest,cost --baseline cost --seeds 20 --first-seed 21 \
  --out docs/results/h4-watchdog-false-positives.md --title "H4: watchdog false positives on fault-free runs vs T (held-out seeds 21-40)" >/dev/null
echo "written docs/results/h3-watchdog-timeout.md docs/results/h4-watchdog-false-positives.md"
