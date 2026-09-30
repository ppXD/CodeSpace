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
gh_requests="${tmp}/gh-requests.log"
mkdir -p "$history" "$stub_bin"

# The `gh` stub answers the only two calls the guard makes — list ONE PAGE of this workflow's completed runs, and
# download one run's log archive. Stubbing the CLI rather than the guard's own lookup keeps the guard's real endpoints,
# its real `unzip -p` streaming and its real census parser under test; only the network is replaced. Every request is
# appended to a log, because "the guard stopped paging" is visible nowhere else.
cat > "${stub_bin}/gh" <<'STUB'
#!/usr/bin/env bash
endpoint="$2"
printf '%s\n' "$endpoint" >> "${GUARD_TEST_GH_LOG:-/dev/null}"

case "$endpoint" in
  *"/runs?branch="*)
    page=1
    case "$endpoint" in *"&page="*) page="${endpoint##*&page=}" ;; esac

    # A guard that keeps asking for pages once the history has run out would loop forever; fail the listing instead,
    # so that regression fails the suite rather than hanging it. No case here stages more than 6 pages.
    if [ "$(grep -c 'page=' "${GUARD_TEST_GH_LOG:-/dev/null}")" -gt 8 ]; then exit 1; fi

    # A page nobody staged is past the end of the history, which the API answers with an empty page — except page 1,
    # where a missing listing stands for the listing call itself failing.
    if [ -f "${GUARD_TEST_HISTORY}/run-ids.${page}" ]; then exec cat "${GUARD_TEST_HISTORY}/run-ids.${page}"; fi
    if [ "$page" -gt 1 ]; then exit 0; fi
    exit 1
    ;;
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

# A run listing is one file per page, `run-ids.<page>`; set_history stages page 1, the only page most cases ever need.
set_history_page() { local page="$1"; shift; printf '%s\n' "$@" > "${history}/run-ids.${page}"; }
set_history() { set_history_page 1 "$@"; }

# The guard as GitHub runs it on the streak branch: run 999 is THIS run and must be skipped in its own history.
# The history read's two fallible prerequisites — the token and the listing endpoint — are parameters, because the
# cases that matter most are the ones where one of them is missing.
run_on_history() {
  local token="$1" history_dir="$2" ref="$3"; shift 3

  : > "$summary"
  : > "$gh_requests"
  env PATH="${stub_bin}:${PATH}" \
    GUARD_TEST_HISTORY="$history_dir" \
    GUARD_TEST_GH_LOG="$gh_requests" \
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

# Which pages of run history the guard asked for, in order. Where paging STOPPED is visible nowhere else: reading page 3
# as well can end in exactly the same verdict, and only the requests tell the two apart.
expect_pages() {
  local want="$1" name="$2" got; shift 2
  "$@" >/dev/null 2>&1
  got="$(sed -nE 's/.*[?&]page=([0-9]+).*/\1/p' "$gh_requests" | tr '\n' ' ' | sed 's/ $//')"

  if [ "$got" = "$want" ]; then
    echo "  ok      ${name}"
  else
    echo "  FAILED  ${name} — the guard asked for history pages '${got}', expected '${want}'"
    failures=$((failures + 1))
  fi
}

# Rule 8: the threshold is a named constant, changed by a PR. Pinned literally, because moving it silently changes
# how long a gate may report green over an instrument that never ran.
expect_output has "readonly DARK_RUNS_TO_RED=3" "the dark-run threshold is pinned at 3" \
  grep -F "readonly DARK_RUNS_TO_RED=3" "$guard"

# The other knobs decide the same thing from the other side: how many runs one page of history holds, how many runs the
# pages may add up to, and whose history counts as a streak at all. Raising the cap silently changes how many censusless
# runs can be stepped over — and how many log archives one guard run downloads; changing the branch silently turns the
# streak off everywhere. Pinned for the same reason as the threshold.
expect_output has "readonly DARK_HISTORY_RUNS_TO_SCAN=12" "the history scan bound is pinned at 12" \
  grep -F "readonly DARK_HISTORY_RUNS_TO_SCAN=12" "$guard"

expect_output has "readonly DARK_HISTORY_RUNS_CAP=60" "the history cap is pinned at 60" \
  grep -F "readonly DARK_HISTORY_RUNS_CAP=60" "$guard"

expect_output has "readonly DARK_STREAK_BRANCH=main" "the streak branch is pinned to main" \
  grep -F "readonly DARK_STREAK_BRANCH=main" "$guard"

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

# ── Paging: the evidence for a streak can sit several pages back ──────────────────────────────────────────────────
#
# A predecessor with no census is stepped over, and a burst of pushes makes exactly those — each push cancels the run
# before it — so the newest page of history can hold fewer census-bearing runs than a streak needs. Read as ONE page,
# that shortfall looked like a short streak: a real main run went green over two UNMEASURED clauses in the middle of a
# 69-run dark streak. The history below is staged as pages of 12 (runs 101..112 are page 1, 201..212 page 2, and so
# on) with every run starting out as a cancelled, census-less one; each case turns only the few it needs into
# census-bearing ones.

# `pages` full pages of 12 census-less runs, and no page beyond them.
reset_paged_history() {
  local pages="$1" page run_id

  rm -f "${history}"/run-ids.*
  make_censusless_predecessor 100

  for page in $(seq 1 "$pages"); do
    set_history_page "$page" $(seq "${page}01" "${page}12")

    for run_id in $(seq "${page}01" "${page}12"); do cp "${history}/100.zip" "${history}/${run_id}.zip"; done
  done
}

# THE regression. Page 1 holds one dark census among eleven cancelled runs, page 2 two more dark ones. One page alone
# read a streak of 2 and stayed green; paged, the guard finds three dark predecessors — a streak of 4 with this run —
# and reds.
reset_paged_history 5
make_predecessor 105 "$dark_trx" RealModelBenchmark
make_predecessor 203 "$dark_trx" RealModelBenchmark
make_predecessor 209 "$dark_trx" RealModelBenchmark

expect 1 "a streak whose evidence sits on page 2 REDS the lane — one page alone read streak 2 and stayed green" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_output has "::error::RealModelBenchmark has now measured NOTHING on 4 consecutive main runs" \
  "the paged streak counts the dark runs of every page it read" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_output has "3 of the 24 predecessor runs scanned carried a census" \
  "the error's evidence base is the whole paged scan, not its first page" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# ...and it read exactly as far as it needed: pages 3-5 are staged and never asked for.
expect_pages "1 2" "paging stops at the page that settles the streak" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# A run that MEASURED the clause settles it alone. Page 1 holds one dark census, page 2 a measuring run: the streak is
# cut at 2 and nothing older can change that, so page 3 — staged with two dark runs — must never be asked for.
reset_paged_history 5
make_predecessor 104 "$dark_trx" RealModelBenchmark
make_predecessor 202 "$measured_trx" RealModelBenchmark
make_predecessor 301 "$dark_trx" RealModelBenchmark
make_predecessor 302 "$dark_trx" RealModelBenchmark

expect 0 "a measuring run on page 2 cuts the streak: the lane only warns" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_pages "1 2" "a measuring run on page 2 stops the paging — page 3 is never asked for" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_summary '| `RealModelBenchmark` | UNMEASURED | 0 | 0 | 1 | 2 |' \
  "the streak it stops at is the one the evidence shows" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_summary "Streaks read from 2 of the 14 predecessor runs scanned" \
  "it stops reading at the run that settled the streak, mid-page" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_output lacks "INCONCLUSIVE" "a settled streak is never reported as inconclusive" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# Nothing settles it. One dark census on page 1 and, on page 3, a census-bearing run whose lane never reported this
# clause, then silence: the scan reads exactly DARK_HISTORY_RUNS_CAP runs — page 6, staged with the two dark runs that
# WOULD red the lane, is never read — and says so, rather than passing the streak of 2 it found off as the whole story.
# Still not red: a run that said nothing is not a dark run.
reset_paged_history 5
make_predecessor 106 "$dark_trx" RealModelBenchmark
make_predecessor 302 "$trx" RealModelSupervisor
make_predecessor 601 "$dark_trx" RealModelBenchmark
make_predecessor 602 "$dark_trx" RealModelBenchmark
set_history_page 6 601 602

expect 0 "a scan that reaches the cap unsettled warns instead of redding the lane" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_output has "is INCONCLUSIVE: only 2 of the 60 predecessor runs scanned carried a census" \
  "an unsettled scan says INCONCLUSIVE, with how many census-bearing runs it found in how many it scanned" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_summary "Streaks read from 2 of the 60 predecessor runs scanned" \
  "the step summary names the same paged evidence base" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_pages "1 2 3 4 5" "the cap is hard: nothing beyond DARK_HISTORY_RUNS_CAP runs is requested" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# A run that finishes while the pages are being read shifts every older run down a slot, so the LAST run of page 1 comes
# back as the FIRST of page 2. A dark run read twice must still count once: here the double count would turn a streak
# of 2 into a red.
reset_paged_history 2
make_predecessor 112 "$dark_trx" RealModelBenchmark
set_history_page 2 112 201 202 203 204 205 206 207 208 209 210 211

expect 0 "a run listed on two pages counts once" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_summary "Streaks read from 1 of the 23 predecessor runs scanned" "a run listed on two pages is scanned once" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# The cap counts RUNS, not pages. That same shifted boundary makes page 2 add only 11 new runs, so five pages come to 59
# and the 60th run is the first of page 6. The scan must take that one run and stop — not the rest of the page, whose
# second run is another dark one that would make the streak 4 over 61 runs.
reset_paged_history 5
make_predecessor 106 "$dark_trx" RealModelBenchmark
make_predecessor 601 "$dark_trx" RealModelBenchmark
make_predecessor 602 "$dark_trx" RealModelBenchmark
set_history_page 2 112 201 202 203 204 205 206 207 208 209 210 211
set_history_page 6 601 602

expect_output has "reds at 3; 2 of the 60 predecessor runs scanned carried a census" \
  "the cap counts runs, not pages: the scan stops at the 60th run, mid-page" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# A census that never mentions the clause — another lane's tables from a run this lane was cancelled in — is evidence of
# nothing for THIS clause, so it cannot count toward "read enough" either. Page 1 holds three census-bearing runs but
# only one dark reading of the clause: counting census-bearing runs would stop there at a streak of 2 and stay green.
# The guard has to read on to page 2, where the second dark reading makes three.
reset_paged_history 5
make_predecessor 101 "$dark_trx" RealModelBenchmark
make_predecessor 102 "$trx" RealModelSupervisor
make_predecessor 103 "$trx" RealModelSupervisor
make_predecessor 201 "$dark_trx" RealModelBenchmark

expect 1 "census-bearing runs that never mention the clause are not enough evidence to stop paging" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_pages "1 2" "...and the paging still stops once the clause's own streak is long enough" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

# A history where no run carried a census (every one cancelled before the guard) gives a streak nothing to be
# consecutive with. That already warned; now it must say how many runs it looked through, or "found no census" reads
# the same after 12 runs as after 60.
reset_paged_history 1

expect 0 "a history with no census at all warns instead of redding the lane" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

expect_output has "could NOT run (INCONCLUSIVE: 0 of the 12 completed main runs scanned carried a clause census" \
  "the no-census warning names how many runs it looked through" \
  run_on refs/heads/main "$dark_trx" RealModelBenchmark

if [ "$failures" -ne 0 ]; then
  echo "${failures} guard self-test(s) failed"
  exit 1
fi

echo "guard self-tests passed"
