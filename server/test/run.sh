#!/bin/bash
# Runs all server tests against a fresh local copy of the server (wrangler dev).
#
#   cd server && npm install && bash test/run.sh
#
# Needs Node.js 22+. Starts the device API on :8787 and the admin API on :8788 (login bypass works
# on localhost only), with an empty local database, then runs each *.test.mjs and stops the servers.
set -u
cd "$(dirname "$0")/.."
W="node node_modules/wrangler/bin/wrangler.js"
LOG="${TMPDIR:-${TEMP:-/tmp}}"

rm -rf .wrangler/state
$W d1 migrations apply signage --local --env device >/dev/null 2>&1 || { echo "migrations failed (did you run npm install?)"; exit 1; }

# Start them one after the other: both share the local database file and can collide if started together.
$W dev --env device --port 8787 --ip 127.0.0.1 >"$LOG/signage-dev-device.log" 2>&1 & DEV_PID=$!
for i in $(seq 1 45); do curl -s -o /dev/null 127.0.0.1:8787/ && break; sleep 2; done
$W dev --env admin --port 8788 --ip 127.0.0.1 --inspector-port 9230 --var DEV_NO_AUTH:1 >"$LOG/signage-dev-admin.log" 2>&1 & ADM_PID=$!
for i in $(seq 1 45); do curl -s -o /dev/null 127.0.0.1:8788/ && break; sleep 2; done

stop() {
  kill $DEV_PID $ADM_PID 2>/dev/null
  command -v pkill >/dev/null && pkill -f "workerd.*serve" 2>/dev/null
  true
}
trap stop EXIT

fail=0
# e2e expects an empty database, so it runs first.
for t in jwt e2e pause bulk video net power; do
  printf '%-7s ' "$t"
  if out=$(node "test/$t.test.mjs" 2>&1); then
    echo "$out" | tail -1
  else
    echo "FAILED"; echo "$out" | grep -E "FAIL|Error" | head -10; fail=1
  fi
done
exit $fail
