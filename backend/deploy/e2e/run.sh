#!/usr/bin/env bash
# Deploy E2E: build + boot the REAL api/worker/Postgres images, then prove — through HTTP, not in-process — that
# the API enqueues and the WORKER runs an agent through its own image to a terminal Success. No model credential or
# network: the worker's harness is pointed at a fake codex. Also asserts both pods' health probes and API leanness.
#
#   backend/deploy/e2e/run.sh            # from anywhere; needs docker compose + node + curl
set -euo pipefail
cd "$(dirname "$0")"

export E2E_JWT_KEY="deploy-e2e-only-symmetric-jwt-key-at-least-32-bytes"
# Test-only AES-256 master key (base64 32 bytes) for variable encryption — required in non-Development.
export E2E_VARIABLE_MASTER_KEY="ECxvpc1Qm6DkzLwTyHnv0OkVj3XaVLVegdoh1NbHziU="
USER_ID="11111111-1111-1111-1111-111111111111"
TEAM_ID="22222222-2222-2222-2222-222222222222"
API="http://localhost:18080"
WORKER="http://localhost:18081"
COMPOSE="docker compose -f docker-compose.e2e.yml"

fail() { echo "❌ $1"; echo "--- worker logs (tail) ---"; $COMPOSE logs --tail=120 worker || true; exit 1; }
cleanup() { $COMPOSE down -v >/dev/null 2>&1 || true; }
trap cleanup EXIT

wait_ready() { # $1=base-url $2=name — readiness only flips 200 after DbUp + the host are up
  for _ in $(seq 1 90); do
    [ "$(curl -fsS -o /dev/null -w '%{http_code}' "$1/health/ready" 2>/dev/null || echo 000)" = "200" ] && { echo "    $2 ready"; return 0; }
    sleep 2
  done
  fail "$2 never became ready at $1/health/ready"
}

# Boot Postgres + the API FIRST so the API runs DbUp once; only then start the worker (against the migrated schema)
# — both pods run DbUp at startup, so serialising avoids a concurrent fresh-DB migration race.
echo "==> build + boot postgres + API (API migrates the schema)"
$COMPOSE up -d --build postgres api
echo "==> health probe: API (anonymous; 200 only after DbUp + host are up)"
wait_ready "$API" "api"

echo "==> boot the worker against the migrated schema"
$COMPOSE up -d --build worker
echo "==> health probe: worker"
wait_ready "$WORKER" "worker"
[ "$(curl -fsS -o /dev/null -w '%{http_code}' "$API/health/live" 2>/dev/null)" = "200" ] || fail "/health/live not 200 (anonymous liveness)"

echo "==> the API image is LEAN (no agent CLIs / git / proxy)"
$COMPOSE exec -T api sh -c '
  for b in codex claude node git bwrap; do command -v "$b" >/dev/null 2>&1 && { echo "LEAK: $b present in API"; exit 1; }; done
  ls codespace-mcp* >/dev/null 2>&1 && { echo "LEAK: codespace-mcp present in API"; exit 1; }
  echo "    API carries zero agent-execution machinery"' || fail "API image is not clean"

echo "==> seed a team + user + membership"
$COMPOSE exec -T postgres psql -U codespace -d codespace -v ON_ERROR_STOP=1 -q < seed.sql || fail "seed failed"
echo "    seeded"

echo "==> launch a quick chat task via the API (enqueue only — the API processes nothing)"
JWT="$(node mint-jwt.js "$E2E_JWT_KEY" "$USER_ID")"
AUTH=(-H "Authorization: Bearer $JWT" -H "X-Team-Id: $TEAM_ID")
RESP="$(curl -fsS -X POST "$API/api/workflows/runs" "${AUTH[@]}" -H "Content-Type: application/json" \
  -d '{"taskText":"Deploy E2E smoke task","effort":"quick","harness":"codex-cli","runnerKind":"local","autonomy":"Confined","surfaceKind":"chat"}')" || fail "launch HTTP call failed"
RUN_ID="$(printf '%s' "$RESP" | sed -n 's/.*"runId":"\([0-9a-f-]*\)".*/\1/p')"
[ -n "$RUN_ID" ] || fail "launch returned no runId: $RESP"
echo "    launched run $RUN_ID"

echo "==> poll until the WORKER drives the agent to a terminal state"
for _ in $(seq 1 80); do
  # The run's own WorkflowRunStatus is the FIRST "status" in the detail JSON (nested node/agent statuses follow).
  STATUS="$(curl -fsS "$API/api/workflows/runs/$RUN_ID" "${AUTH[@]}" | grep -o '"status":"[A-Za-z]*"' | head -1 | sed 's/.*:"\([A-Za-z]*\)"/\1/')"
  echo "    status=$STATUS"
  case "$STATUS" in
    Success) echo "✅ the API enqueued and the WORKER ran the agent through its real image to Success"; exit 0 ;;
    Failure|Cancelled) fail "run reached terminal $STATUS (expected Success)" ;;
  esac
  sleep 3
done
fail "run never reached a terminal state within the timeout"
