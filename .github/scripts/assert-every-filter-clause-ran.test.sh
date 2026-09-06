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

# A FAILED test is a measurement — the loudest kind. A clause with failures alongside skips must NOT be called
# unmeasured, or the "measured nothing" notice buries the regression the lane just caught.
mixed_trx="${tmp}/mixed.trx"
cat > "$mixed_trx" <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun>
  <Results>
    <UnitTestResult testName="CodeSpace.IntegrationTests.Workflows.Supervisor.RealModelSupervisorDecisionFlowTests.The_real_model_decides(provider: &quot;Anthropic&quot;)" outcome="Failed" />
    <UnitTestResult testName="CodeSpace.IntegrationTests.Workflows.Supervisor.RealModelSupervisorDecisionFlowTests.The_real_model_decides(provider: &quot;OpenAI&quot;)" outcome="NotExecuted" />
  </Results>
</TestRun>
XML

expect_output lacks "::warning::" "never warns about a clause whose tests FAILED (a failure is a measurement)" \
  bash "$guard" "$mixed_trx" RealModelSupervisor

expect_output lacks "UNMEASURED" "reports a failed+skipped clause as measured, not unmeasured" \
  bash "$guard" "$mixed_trx" RealModelSupervisor

# ...and the guard itself still exits 0 there: the TEST outcome reds the job, never this guard.
expect 0 "a failed+skipped clause does not fail the guard" \
  bash "$guard" "$mixed_trx" RealModelSupervisor

# ── The consecutive-dark streak: a clause that measures nothing run after run reds the lane ──────────────────────
#
# A one-off skip warns; a STREAK is a broken instrument reporting green, and the warning above had no notion of
# persistence — RealModelBenchmark stayed dark for five consecutive main runs, ~30 min of live API budget each, while
# the lane read green every time.
#
# The streak is read back out of the predecessor runs' own logs, so the fixtures here are REAL ones: each synthetic
# predecessor log is produced by RUNNING THIS GUARD and stamping GitHub's timestamp prefix onto its output. Drift in
# the census table's shape therefore drifts the fixture with it, and the parser is always tested against exactly what
# the printer prints — never against a hand-typed approximation of it.

# A predecessor's log arrives as a zip from the run-log endpoint, so building one needs zip and reading it needs
# unzip. Both ship on the runner image — but if either ever stops shipping, say so instead of letting every streak
# case quietly assert nothing, which is the failure mode this whole section exists to abolish.
if ! command -v zip >/dev/null 2>&1 || ! command -v unzip >/dev/null 2>&1; then
  echo "  FAILED  the streak self-tests need zip AND unzip to build and read a predecessor log archive"
  failures=$((failures + 1))
fi

history="${tmp}/history"
stub_bin="${tmp}/stub-bin"
summary="${tmp}/step-summary.md"
mkdir -p "$history" "$stub_bin"

# The `gh` stub answers the only two calls the guard makes — list this workflow's completed runs, and download one
# run's log archive. Stubbing the CLI rather than the guard's own lookup keeps the guard's real endpoints, its real
# `unzip -p` streaming and its real census parser under test; only the network is replaced.
cat > "${stub_bin}/gh" <<'STUB'
#!/usr/bin/env bash
endpoint="$2"
case "$endpoint" in
  *"/runs?branch="*) cat "${GUARD_TEST_HISTORY}/run-ids" ;;
  */logs) run_id="${endpoint%/logs}"; exec cat "${GUARD_TEST_HISTORY}/${run_id##*/}.zip" ;;
  *) exit 1 ;;
esac
STUB
chmod +x "${stub_bin}/gh"

dark_trx="${tmp}/dark.trx"
cat > "$dark_trx" <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun>
  <Results>
    <UnitTestResult testName="CodeSpace.E2ETests.Workflows.RealModelBenchmarkCorpusE2ETests.A_real_coding_agent_runs_the_seed_corpus" outcome="NotExecuted">
      <Output>
        <ErrorInfo>
          <Message>real-model gate NON-GATING infra skip: evaluator health 50 % (infra-dead cells 9/18) below the 90 % floor</Message>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
  </Results>
</TestRun>
XML

measured_trx="${tmp}/measured.trx"
cat > "$measured_trx" <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun>
  <Results>
    <UnitTestResult testName="CodeSpace.E2ETests.Workflows.RealModelBenchmarkCorpusE2ETests.A_real_coding_agent_runs_the_seed_corpus" outcome="Passed" />
  </Results>
</TestRun>
XML

# One predecessor run's log archive: this guard's own census output, timestamp-prefixed the way GitHub's log API
# returns it, zipped the way the run-log endpoint serves it.
make_predecessor() {
  local run_id="$1" src_trx="$2"; shift 2
  local dir="${tmp}/pred-${run_id}"

  rm -rf "$dir" && mkdir -p "$dir"
  bash "$guard" "$src_trx" "$@" 2>&1 | sed 's/^/2026-09-01T00:00:00.0000000Z /' > "${dir}/0_real model (a lane).txt"
  rm -f "${history}/${run_id}.zip"
  (cd "$dir" && zip -qq "${history}/${run_id}.zip" "0_real model (a lane).txt")
}

# A run whose job never reached the guard — cancelled by concurrency, or red before it. Its log carries no census, so
# it is evidence of NOTHING and must be stepped over rather than counted as a reset.
make_censusless_predecessor() {
  local run_id="$1"
  local dir="${tmp}/pred-${run_id}"

  rm -rf "$dir" && mkdir -p "$dir"
  printf '2026-09-01T00:00:00.0000000Z The operation was canceled.\n' > "${dir}/0_real model (a lane).txt"
  rm -f "${history}/${run_id}.zip"
  (cd "$dir" && zip -qq "${history}/${run_id}.zip" "0_real model (a lane).txt")
}

set_history() { printf '%s\n' "$@" > "${history}/run-ids"; }

# The guard as GitHub runs it on the streak branch: run 999 is THIS run and must be skipped in its own history.
# The history read's two fallible prerequisites — the token and the listing endpoint — are parameters, because the
# cases that matter most are the ones where one of them is missing.
run_on_history() {
  local token="$1" history_dir="$2" ref="$3"; shift 3

  : > "$summary"
  env PATH="${stub_bin}:${PATH}" \
    GUARD_TEST_HISTORY="$history_dir" \
    GH_TOKEN="$token" \
    GITHUB_TOKEN="$token" \
    GITHUB_REF="$ref" \
    GITHUB_REPOSITORY=owner/repo \
    GITHUB_RUN_ID=999 \
    GITHUB_WORKFLOW_REF='owner/repo/.github/workflows/real-model.yml@refs/heads/main' \
    GITHUB_STEP_SUMMARY="$summary" \
    bash "$guard" "$@"
}

run_on() { run_on_history stub-token "$history" "$@"; }

expect_summary() {
  local needle="$1" name="$2"; shift 2
  "$@" >/dev/null 2>&1

  if grep -qF -- "$needle" "$summary"; then
    echo "  ok      ${name}"
  else
    echo "  FAILED  ${name} — step summary lacks '${needle}'"
    sed 's/^/          | /' "$summary"
    failures=$((failures + 1))
  fi
}

# Rule 8: the threshold is a named constant, changed by a PR. Pinned literally, because moving it silently changes
# how long a gate may report green over an instrument that never ran.
expect_output has "readonly DARK_RUNS_TO_RED=3" "the dark-run threshold is pinned at 3" \
  grep -F "readonly DARK_RUNS_TO_RED=3" "$guard"

# The history parser recovers a predecessor's census by matching the table header this guard prints. If the printer
# and the matcher ever disagree, every predecessor silently becomes "no evidence" and the streak never grows.
expect_output has "outcome   passed    failed    skipped   clause" "prints the exact census header its history parser matches" \
  bash "$guard" "$trx" RealModelSupervisor

set_history 999 111 222 333

# Streak 2 — one dark predecessor plus this run. Below the threshold, so today's warning still stands alone.
make_predecessor 111 "$dark_trx" RealModelBenchmark
make_predecessor 222 "$measured_trx" RealModelBenchmark
make_predecessor 333 "$measured_trx" RealModelBenchmark

expect 0 "two consecutive dark runs still only warn" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_output lacks "::error::" "two consecutive dark runs emit no error" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_summary '| `RealModelBenchmark` | UNMEASURED | 0 | 0 | 1 | 2 |' "the step summary carries the streak per clause" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# Streak 3 — THE case this exists for. The lane has now spent three full live-API budgets measuring nothing.
make_predecessor 222 "$dark_trx" RealModelBenchmark

expect 1 "three consecutive dark runs RED the lane" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_output has "::error::RealModelBenchmark has now measured NOTHING on 3 consecutive main runs" \
  "the error names the clause and the streak" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_output has "evaluator health 50 % (infra-dead cells 9/18) below the 90 % floor" \
  "the error names the last recorded skip reason" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# Fail OPEN. The history read is this guard's own instrument, and an instrument that cannot read must red nothing:
# a denied `actions: read`, a token the workflow forgot to pass, a listing endpoint that 500s. Each leaves today's
# one-off warning standing and says WHICH prerequisite was missing — otherwise the guard becomes the silent nothing
# it exists to abolish. The history staged above is the streak-3 one that DOES red, so every case below is that same
# red minus the ability to read the evidence for it.
expect 0 "a step with no token warns instead of redding the lane" \
  run_on_history "" "$history" refs/heads/main "$dark_trx" RealModelBenchmark

expect_output has "could NOT run (no GH_TOKEN/GITHUB_TOKEN on this step" \
  "a step with no token names the missing token as the reason the check could not run" \
  run_on_history "" "$history" refs/heads/main "$dark_trx" RealModelBenchmark

# ...and the streak cell says so too, rather than reporting a confident 0 nobody measured.
expect_summary '| `RealModelBenchmark` | UNMEASURED | 0 | 0 | 1 | not checked |' \
  "an unreadable history reports 'not checked', never a fabricated streak" \
  run_on_history "" "$history" refs/heads/main "$dark_trx" RealModelBenchmark

# The same fail-open one layer down: the token is there, the listing itself fails (no `actions: read`, or a 5xx).
unlistable="${tmp}/unlistable-history"
mkdir -p "$unlistable"

expect 0 "a run-history listing that FAILS warns instead of redding the lane" \
  run_on_history stub-token "$unlistable" refs/heads/main "$dark_trx" RealModelBenchmark

expect_output has "could NOT run (the workflow's run history could not be listed" \
  "a failed listing names the listing as the reason, pointing at \`actions: read\`" \
  run_on_history stub-token "$unlistable" refs/heads/main "$dark_trx" RealModelBenchmark

expect_output lacks "::error::" "a failed listing emits no error at all" \
  run_on_history stub-token "$unlistable" refs/heads/main "$dark_trx" RealModelBenchmark

# A branch run has no streak to be consecutive with, so the same history must never red it.
expect 0 "a run off the streak branch never reds on a streak" \
  run_on refs/heads/feature/whatever "$dark_trx" RealModelBenchmark

# A run that never reached the guard recorded no census. Counting its silence as a reset would let one cancelled run
# launder a five-run streak — the exact laundering that kept RealModelBenchmark's darkness invisible.
make_censusless_predecessor 111
make_predecessor 333 "$dark_trx" RealModelBenchmark

expect 1 "a run with no census is stepped over, not counted as a measurement" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# ...and the report says how thin that evidence is. "Streak 3" over three censuses and "streak 3" over one census
# and a dozen silent runs are the same number describing opposite situations, so both counts travel with it: here
# three predecessors were scanned and only two of them carried a census at all.
expect_output has "2 of the 3 predecessor runs scanned carried a census" \
  "the error names how much evidence the streak actually rests on" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_summary "Streaks read from 2 of the 3 predecessor runs scanned" \
  "the step summary names the same evidence base as the error" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# The same laundering one step subtler, and the one a live rehearsal actually caught: a run this lane was cancelled
# in still ships a census — its OTHER lanes' tables — so the archive is not empty, the clause is merely ABSENT from
# it. Reading that absence as "measured" reset a real four-run streak back to one.
make_predecessor 111 "$trx" RealModelSupervisor

expect 1 "a census that never mentions the clause is stepped over too" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# ...and a run that actually measured the clause resets it, however long the darkness behind that run was.
make_predecessor 111 "$measured_trx" RealModelBenchmark

expect 0 "a measured run resets the streak" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_output lacks "::error::" "a measured run leaves only the one-off warning" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# A clause that measured something is never on a streak at all, whatever its neighbours did.
expect_summary '| `RealModelBenchmark` | ok | 1 | 0 | 0 | 0 |' "a measured clause reports a zero streak" \
  run_on refs/heads/main "$measured_trx" RealModelBenchmark

# A predecessor whose census recorded the clause MISSING breaks the streak, and deliberately so: MISSING means the
# clause selected no test at all, which exits the guard RED on the spot — that run already told a human, and the
# streak counts runs that measured nothing while reporting GREEN. Pinned because the two readings differ by three
# live-API budgets: 222 and 333 are still dark behind this run, so reading MISSING as no-evidence would red the lane.
make_predecessor 111 "$trx" RealModelBenchmark

expect 0 "a predecessor that recorded the clause MISSING breaks the streak — that run was already red" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# ...and the streak it reports is the one a human can check: 1, over the evidence of exactly one predecessor.
expect_summary '| `RealModelBenchmark` | UNMEASURED | 0 | 0 | 1 | 1 |' \
  "a MISSING predecessor resets the streak to 1 rather than stepping over it" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

if [ "$failures" -ne 0 ]; then
  echo "${failures} guard self-test(s) failed"
  exit 1
fi

echo "guard self-tests passed"
