#!/usr/bin/env bash
# Latency curves for docs/results.md: each (configuration, burst size) point runs in a fresh process with its own
# warm-up, so points do not inherit GC / thread-pool state from each other. Run on a quiet machine.
# Usage: scripts/perf-curves.sh [repeats]   (default 3; the median p99 of the repeats is reported)
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build -c Release -v q src/Elevator.Cli >/dev/null
E=src/Elevator.Cli/bin/Release/net10.0/elevator
REPEATS=${1:-3}
LEVELS=(100 300 1000 3000 5000 6500 8000 10000)
OUT=docs/results/perf-curves.md
TMP=$(mktemp -d)

declare -A CONFIGS=(
  ["default (cost + fallback above 200)"]=""
  ["cost only"]="--overload-threshold none"
  ["nearest only"]="--dispatch nearest --overload-threshold none"
)

{
  echo "# Perf: assignment latency vs simultaneous submitters"
  echo
  echo "- **Command:** \`scripts/perf-curves.sh $REPEATS\`"
  echo "- **Machine:** $(sysctl -n machdep.cpu.brand_string 2>/dev/null || uname -m), $(sysctl -n hw.logicalcpu 2>/dev/null) logical cores, load average at start: $(sysctl -n vm.loadavg 2>/dev/null | awk '{print $2}')"
  echo "- **System:** look scheduling, 4 elevators, 20 floors, capacity 10, next-trip; each point a fresh process with warm-up; $REPEATS repeats, median by p99."
  echo
  echo "Wall-clock ms from AssignRequest to accepted assignment (HdrHistogram). req/s = assigned / wall time of the burst."
  echo
  for name in "default (cost + fallback above 200)" "cost only" "nearest only"; do
    echo "## $name"
    echo
    echo "| Submitters | p50 ms | p95 ms | p99 ms | max ms | req/s | lost | dup | decided by fallback |"
    echo "|---|---|---|---|---|---|---|---|---|"
    for level in "${LEVELS[@]}"; do
      rows=()
      for r in $(seq 1 "$REPEATS"); do
        # shellcheck disable=SC2086
        $E perf --levels "$level" --soak-seconds 0 ${CONFIGS[$name]} --out "$TMP/p.md" >/dev/null 2>&1 || true
        rows+=("$(grep -E "^\| $level \|" "$TMP/p.md" | head -1)")
      done
      # median by p99 (column 9 in the perf table)
      printf '%s\n' "${rows[@]}" | sort -t'|' -k9 -g | sed -n "$(( (REPEATS + 1) / 2 ))p" \
        | awk -F'|' '{printf "| %s | %s | %s | %s | %s | %s | %s | %s | %s |\n",$2,$7,$8,$9,$10,$11,$5,$6,$12}' | sed 's/  */ /g'
    done
    echo
  done
} > "$OUT"
echo "wrote $OUT"
