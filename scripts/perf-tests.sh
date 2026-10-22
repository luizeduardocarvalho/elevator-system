#!/usr/bin/env bash
# The wall-clock perf tests (Category=Perf: the perf gate, the soak and the ramp smoke test), run alone. They are
# skipped by plain `dotnet test` because the other test projects run in parallel processes and starve their threads.
# Run on a quiet machine.
# Usage: scripts/perf-tests.sh
set -euo pipefail
cd "$(dirname "$0")/.."
ELEVATOR_PERF=1 dotnet test tests/Elevator.Lab.Tests --filter "Category=Perf"
