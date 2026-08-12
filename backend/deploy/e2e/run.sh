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
# Must match seed.sql — the API compares it against the row on every request.
SECURITY_STAMP="33333333-4444-5555-6666-777777777777"
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

# The API image carries no agent-EXECUTION machinery. git is the ONE sanctioned exception, documented in
# Dockerfile.api's header (S1): TaskLaunchService resolves a launch's immutable base vector synchronously via
# `git ls-remote` (RemoteTipResolver) — read-only, no clone, no working tree. This check forbade it anyway and had
# been red on main since 2026-07-11, unnoticed because the job only runs when these paths change.
#
# So git is asserted PRESENT, not merely allowed: the exception is deliberate, and an image that lost git would
# break launch. Removing the ls-remote dependency should red here and force this contract to be re-stated, rather
# than silently widening what "lean" means.
echo "==> the API image is LEAN (no agent CLIs / proxy; git present for ls-remote only)"
$COMPOSE exec -T api sh -c '
  for b in codex claude node bwrap; do command -v "$b" >/dev/null 2>&1 && { echo "LEAK: $b present in API"; exit 1; }; done
  ls codespace-mcp* >/dev/null 2>&1 && { echo "LEAK: codespace-mcp present in API"; exit 1; }
  command -v git >/dev/null 2>&1 || { echo "MISSING: git absent from API — launch base-vector resolution (git ls-remote) cannot work"; exit 1; }
  echo "    API carries zero agent-execution machinery; git present for ls-remote only"' || fail "API image is not clean"

# The mirror of the leanness check: the worker must carry EVERY binary the agent-execution path spawns by name from
# C#. The Dockerfile asserts this at build time too; asserting it again on the RUNNING container catches anything a
# later stage strips (a COPY that overwrites /usr/local/bin, a squash step). The egress trio is the reason this
# matters beyond exit-127: with ip/nft/sysctl absent, SandboxEgressPolicy fails CLOSED and an allowlisted run gets
# NO network instead of a filtered one — correct, but the feature is silently unusable.
echo "==> the WORKER image is COMPLETE (every spawned binary resolves)"
$COMPOSE exec -T worker sh -c '
  for b in git git-lfs bwrap prlimit setsid ip nft sysctl curl node codex claude; do
    command -v "$b" >/dev/null 2>&1 || { echo "MISSING: $b — the worker spawns it by name"; exit 1; }
  done
  [ -x ./codespace-mcp ] || { echo "MISSING: codespace-mcp — the MCP endpoint would fail closed to tool-less runs"; exit 1; }
  echo "    worker carries every agent-execution binary + the MCP proxy"' || fail "worker image is missing a dependency"

echo "==> seed a team + user + membership"
$COMPOSE exec -T postgres psql -U codespace -d codespace -v ON_ERROR_STOP=1 -q < seed.sql || fail "seed failed"
echo "    seeded"

echo "==> launch a quick chat task via the API (enqueue only — the API processes nothing)"
JWT="$(node mint-jwt.js "$E2E_JWT_KEY" "$USER_ID" "$SECURITY_STAMP")"
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
