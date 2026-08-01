#!/usr/bin/env bash
#
# The guard is the only thing standing between a de-selected class and a green lane, so it needs its own teeth
# checked. A guard that always passes is indistinguishable from no guard, and that is exactly the failure it exists
# to catch — so every case below asserts the EXIT CODE, not the output.
#
# Run: bash .github/scripts/assert-every-filter-clause-ran.test.sh

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
guard="${here}/assert-every-filter-clause-ran.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

trx="${tmp}/sample.trx"
cat > "$trx" <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun>
  <Results>
    <UnitTestResult testName="CodeSpace.IntegrationTests.Workflows.Supervisor.RealModelSupervisorDecisionFlowTests.The_real_model_decides(provider: &quot;Anthropic&quot;)" outcome="Passed" />
    <UnitTestResult testName="CodeSpace.IntegrationTests.Sessions.RealModelSessionFlowTests.A_session_continues" outcome="Passed" />
  </Results>
</TestRun>
XML

failures=0

expect() {
  local want="$1" name="$2"; shift 2
  "$@" >/dev/null 2>&1
  local got=$?
  if [ "$got" -eq "$want" ]; then
    echo "  ok      ${name}"
  else
    echo "  FAILED  ${name} — expected exit ${want}, got ${got}"
    failures=$((failures + 1))
  fi
}

# Every clause present → pass. Without this the guard could be red-always, which is just as useless as green-always.
expect 0 "passes when every clause selected a test" \
  bash "$guard" "$trx" RealModelSupervisor RealModelSession

# THE case this exists for: one clause of several selected nothing. A count-based guard passes here (2 tests ran);
# this one must not.
expect 1 "fails when ONE clause of several selected nothing" \
  bash "$guard" "$trx" RealModelSupervisor RealModelSession RealModelPublishManifest

expect 1 "fails when no clause matched at all" \
  bash "$guard" "$trx" RealModelNothingAtAll

# A substring that appears in the FQN but is not a class token would be a false pass; the guard matches the same way
# `FullyQualifiedName~` does, so this SHOULD pass — pinned so the correspondence is deliberate, not accidental.
expect 0 "matches a partial token exactly as FullyQualifiedName~ does" \
  bash "$guard" "$trx" RealModelSupervisorDecision

# Infrastructure faults must be loud, never a silent pass.
expect 1 "fails when the trx is missing entirely" \
  bash "$guard" "${tmp}/absent.trx" RealModelSupervisor

expect 1 "fails when given no clauses to check" \
  bash "$guard" "$trx"

if [ "$failures" -ne 0 ]; then
  echo "${failures} guard self-test(s) failed"
  exit 1
fi

echo "guard self-tests passed"
