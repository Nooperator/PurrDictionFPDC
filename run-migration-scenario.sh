#!/usr/bin/env bash
set -u

ROOT="C:/wkspaces/unity/riten/PurrDiction"
BIN="$ROOT/build/StandaloneWindows64/PurrDictionTests.exe"
PORT="${PORT:-37101}"
COUNT=3
RESULTS="$ROOT/test-results/migration"
LOGS="$ROOT/test-logs/migration"

rm -rf "$RESULTS" "$LOGS"
mkdir -p "$RESULTS" "$LOGS"

"$BIN" -batchmode -nographics -role server -count $COUNT -port "$PORT" \
    -connectTimeout 180 -latencyMin 40 -latencyMax 80 -hostMigrationScenarioOnly \
    -results "$RESULTS/server.json" -logFile "$LOGS/server.log" &
SPID=$!

sleep 4

CPIDS=()
for i in 1 2 3; do
    "$BIN" -batchmode -nographics -role client -count $COUNT -port "$PORT" \
        -connectTimeout 180 -latencyMin 40 -latencyMax 80 -hostMigrationScenarioOnly \
        -results "$RESULTS/client-$i.json" -logFile "$LOGS/client-$i.log" &
    CPIDS+=("$!")
    sleep 1
done

(
    sleep 360
    echo "TIMEOUT: killing processes"
    taskkill //IM PurrDictionTests.exe //F 2>/dev/null || true
) &
WATCHDOG=$!

FAIL=0
for pid in "$SPID" "${CPIDS[@]}"; do
    wait "$pid" || FAIL=1
done
kill "$WATCHDOG" 2>/dev/null || true

for f in "$RESULTS"/*.json; do
    echo "== $f"
    python - "$f" <<'EOF'
import json, sys
for x in json.load(open(sys.argv[1])):
    ok = x["result"]["success"]
    print(x["name"], "PASS" if ok else "FAIL: " + str(x["result"].get("message", ""))[:600])
EOF
done
exit $FAIL
