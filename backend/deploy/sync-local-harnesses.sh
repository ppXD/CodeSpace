#!/usr/bin/env bash
# Sync the LOCAL agent-CLI harnesses to the EXACT versions the worker image pins — so a dev box runs the same codex
# /claude the worker does, and the harness-reported version (CodexHarness/ClaudeCodeHarness.DefaultVersion, pinned to
# these same ARGs by HarnessVersionPinTests) stays honest. The single source of truth is backend/Dockerfile.worker.
#
#   backend/deploy/sync-local-harnesses.sh
#
# After a version bump, the lockstep is: edit the Dockerfile ARG → update the matching DefaultVersion C# const (the
# pin test enforces this) → run this script on every dev box. Nothing to remember beyond "bump the ARG, run this".
set -euo pipefail

DOCKERFILE="$(cd "$(dirname "$0")/.." && pwd)/Dockerfile.worker"
[ -f "$DOCKERFILE" ] || { echo "✗ $DOCKERFILE not found"; exit 1; }

codex_v=$(grep -oE 'ARG CODEX_CLI_VERSION=[^[:space:]]+' "$DOCKERFILE" | head -1 | cut -d= -f2)
claude_v=$(grep -oE 'ARG CLAUDE_CODE_VERSION=[^[:space:]]+' "$DOCKERFILE" | head -1 | cut -d= -f2)
echo "Worker-pinned versions: codex=$codex_v  claude=$claude_v"

npm install -g "@openai/codex@${codex_v}" "@anthropic-ai/claude-code@${claude_v}"

# Verify the EFFECTIVE (PATH) binaries match — a native claude/codex install can shadow the npm one.
check() { # $1=binary $2=expected
  local got; got=$("$1" --version 2>/dev/null | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1 || echo "missing")
  if [ "$got" = "$2" ]; then echo "✓ $1 $got"; else
    echo "⚠ $1 on PATH is $got, expected $2 — a native install may be shadowing npm. Update it (e.g. 'claude update') or fix PATH order."
  fi
}
check codex "$codex_v"
check claude "$claude_v"
