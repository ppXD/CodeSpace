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

# A METHOD-level token, not just a class. The injection lane's reason to exist is one specific arm, and a drift that
# de-selects only that arm satisfies every class-level clause — so the guard has to work at this grain too. It does,
# with no special casing, because the fully-qualified name it greps carries the method.
expect 0 "matches a method-level token" \
  bash "$guard" "$trx" The_real_model_decides

expect 1 "fails when a method-level token selected nothing" \
  bash "$guard" "$trx" RealModelSupervisor A_method_that_was_de_selected

# Infrastructure faults must be loud, never a silent pass.
expect 1 "fails when the trx is missing entirely" \
  bash "$guard" "${tmp}/absent.trx" RealModelSupervisor

expect 1 "fails when given no clauses to check" \
  bash "$guard" "$trx"

# ── Outcome counting: "the clause selected a test" is not "the clause measured anything" ──────────────────────────
#
# The real-model gates now SKIP (NotExecuted) when there are no live credentials or the gateway faulted, so a lane
# can select every test it claims to and still measure nothing. That case must WARN — and must NOT error, because a
# non-gating infra skip is by design.

skipped_trx="${tmp}/skipped.trx"
cat > "$skipped_trx" <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun>
  <Results>
    <UnitTestResult testName="CodeSpace.IntegrationTests.Workflows.Supervisor.RealModelSupervisorDecisionFlowTests.The_real_model_decides(provider: &quot;Anthropic&quot;)" outcome="NotExecuted" />
    <UnitTestResult testName="CodeSpace.IntegrationTests.Sessions.RealModelSessionFlowTests.A_session_continues" outcome="Passed" />
  </Results>
</TestRun>
XML

expect_output() {
  local mode="$1" needle="$2" name="$3"; shift 3
  local out
  out="$("$@" 2>&1)"

  if [ "$mode" = "has" ] && printf '%s' "$out" | grep -qF -- "$needle"; then
    echo "  ok      ${name}"
  elif [ "$mode" = "lacks" ] && ! printf '%s' "$out" | grep -qF -- "$needle"; then
    echo "  ok      ${name}"
  else
    echo "  FAILED  ${name} — output ${mode} '${needle}' was not satisfied"
    printf '%s\n' "$out" | sed 's/^/          | /'
    failures=$((failures + 1))
  fi
}

# THE new case: every test the clause selected skipped, so the clause measured nothing.
expect_output has "::warning::" "warns when a clause's tests ALL skipped" \
  bash "$guard" "$skipped_trx" RealModelSupervisor RealModelSession

expect_output has "RealModelSupervisor(1 skipped)" "names WHICH clause measured nothing" \
  bash "$guard" "$skipped_trx" RealModelSupervisor RealModelSession

# A skip is non-gating by design — the warning must never become an error.
expect 0 "an all-skipped clause does NOT fail the job" \
  bash "$guard" "$skipped_trx" RealModelSupervisor RealModelSession

# A clause with a passing test alongside is measured; it must not be dragged into the warning.
expect_output lacks "RealModelSession(" "does not warn about a clause that passed" \
  bash "$guard" "$skipped_trx" RealModelSupervisor RealModelSession

# A fully healthy lane warns about nothing at all.
expect_output lacks "::warning::" "never warns when every clause measured something" \
  bash "$guard" "$trx" RealModelSupervisor RealModelSession

# The per-clause outcome table is the artefact a human reads to tell a 5-minute run from a 1-second one.
expect_output has "skipped" "prints a per-clause outcome table" \
  bash "$guard" "$trx" RealModelSupervisor

if [ "$failures" -ne 0 ]; then
  echo "${failures} guard self-test(s) failed"
  exit 1
fi

echo "guard self-tests passed"
