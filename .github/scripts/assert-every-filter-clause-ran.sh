#!/usr/bin/env bash
#
# Fail unless EVERY `FullyQualifiedName~<token>` clause of a lane's --filter actually selected a test, and REPORT what
# each clause's tests actually did (passed / failed / skipped).
#
# Why this exists, twice over: `dotnet test --filter` exits 0 when the filter matches ZERO tests, so a renamed or
# moved class silently stops running and its lane stays green. The previous guard counted total executed tests and
# required >= 1 — which cannot see the real failure mode. A lane selecting four classes that drops to three still
# reports a positive count, and the two times this actually happened (the session evals, then the Custom-provider
# gate) both had a lane whose OTHER clauses kept the count healthy while the dropped one ran nowhere at all.
#
# Why it now counts OUTCOMES too: "the clause selected a test" is not "the clause measured anything". The real-model
# gates skip (NotExecuted) when there are no live credentials or the gateway faulted, so a lane where every blessed
# test skipped selected plenty of tests and measured nothing. That is surfaced as a ::warning:: — deliberately NOT an
# error, because a non-gating infra skip is by design (see RealModelGate) — but it is no longer invisible.
#
# Why it now counts CONSECUTIVE darkness: that warning has no notion of persistence, and a warning nobody has to act
# on decays into wallpaper. RealModelBenchmark — the only whole-corpus solve-rate gate there is — was UNMEASURED for
# five consecutive main runs (evaluator health 50 %, 9/18 infra-dead cells, root cause a gateway fault), each run
# still burning ~30 min of live API budget, while the lane reported green every single time. A one-off skip is infra;
# a streak is a BROKEN INSTRUMENT, and an instrument that has measured nothing for DARK_RUNS_TO_RED runs running has
# to red the lane it is supposed to protect. The streak is read out of the PREVIOUS runs' own logs — see the history
# section below — so it needs no new secret, no new artifact, and no seeding period.
#
# The check greps `testName`, which carries the fully-qualified name — the same string `FullyQualifiedName~` matched
# on — so the guard and the filter can never disagree about what a clause means.
#
# Usage: assert-every-filter-clause-ran.sh <trx-path> <token> [<token>...]
#   where each <token> is exactly the text after `FullyQualifiedName~` in that lane's own --filter.

set -euo pipefail

# ── Knobs (Rule 8 shape: named constants, pinned by the self-test, changed by a PR — never by an env override) ─────

# How many CONSECUTIVE runs on the streak branch (this one included) may report a clause UNMEASURED before the lane
# goes red. Below it the run keeps today's warning: one dark run really can be a one-off gateway fault.
readonly DARK_RUNS_TO_RED=3

# How many completed runs to page through while looking for census-bearing predecessors. A run whose job never
# reached this guard — cancelled by concurrency, or red before it — recorded no census and is therefore evidence of
# NOTHING; it is stepped over rather than counted as a reset, and this bound keeps that stepping-over from paging
# back through the whole history.
readonly DARK_HISTORY_RUNS_TO_SCAN=12

# Only this branch carries a streak. A branch run is a one-off by construction — it has no predecessors to be
# consecutive with — so it keeps the warning and never reds on persistence.
readonly DARK_STREAK_BRANCH=main

# The census table's header, printed before the first row and therefore present in the log of EVERY run that reached
# this guard, including one that exits red a moment later. It is the marker a predecessor's log must carry for its
# census to count as evidence at all, which is why it is a constant: the same string is printed and matched.
readonly CENSUS_HEADER_ROW='outcome   passed    failed    skipped   clause'

trx="${1:?usage: $0 <trx-path> <token> [<token>...]}"
shift

if [ ! -f "$trx" ]; then
  echo "::error::trx not found at $trx — the test step produced no results at all"
  exit 1
fi

if [ "$#" -eq 0 ]; then
  echo "::error::no filter-clause tokens given; this guard cannot vouch for anything"
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

censuses="${work}/censuses"
mkdir -p "$censuses"

# This run's own census, "<token>|<state>|<passed>|<failed>|<skipped>" per clause, for the step summary.
rows="${work}/rows"
: > "$rows"

# ── Census ────────────────────────────────────────────────────────────────────────────────────────────────────────

# One line per result: "<outcome> <fully-qualified test name>". A trx <UnitTestResult> carries both attributes on its
# opening tag, testName first, so a single pass over the element text pairs them without an XML parser.
results="$(grep -o 'testName="[^"]*"[^>]*outcome="[^"]*"' "$trx" \
  | sed -E 's/^testName="([^"]*)".*outcome="([^"]*)"$/\2 \1/' || true)"

missing=""
unmeasured=""
unmeasured_tokens=""

printf '  %-9s %-9s %-9s %-9s %s\n' outcome passed failed skipped clause
for token in "$@"; do
  matched="$(printf '%s\n' "$results" | grep -F -- "$token" || true)"
  total="$(printf '%s' "$matched" | grep -c . || true)"
  passed="$(printf '%s\n' "$matched" | grep -c '^Passed ' || true)"
  failed="$(printf '%s\n' "$matched" | grep -c '^Failed ' || true)"
  skipped="$(printf '%s\n' "$matched" | grep -c '^NotExecuted ' || true)"

  if [ "$total" -eq 0 ]; then
    printf '  %-9s %-9s %-9s %-9s %s\n' MISSING - - - "$token"
    printf '%s|MISSING|-|-|-\n' "$token" >> "$rows"
    missing="${missing} ${token}"
    continue
  fi

  # "Measured nothing" means NOTHING ran to a verdict — a FAILED test is a measurement (a loud one), so a clause with
  # failures is not unmeasured no matter how many of its siblings skipped. Without the `failed` term the warning fired
  # on exactly the lane that had just caught a real regression, burying it under a "measured nothing" notice.
  if [ "$passed" -eq 0 ] && [ "$failed" -eq 0 ] && [ "$skipped" -gt 0 ]; then
    printf '  %-9s %-9s %-9s %-9s %s\n' UNMEASURED "$passed" "$failed" "$skipped" "$token"
    printf '%s|UNMEASURED|%s|%s|%s\n' "$token" "$passed" "$failed" "$skipped" >> "$rows"
    unmeasured="${unmeasured} ${token}(${skipped} skipped)"
    unmeasured_tokens="${unmeasured_tokens} ${token}"
    continue
  fi

  printf '  %-9s %-9s %-9s %-9s %s\n' ok "$passed" "$failed" "$skipped" "$token"
  printf '%s|ok|%s|%s|%s\n' "$token" "$passed" "$failed" "$skipped" >> "$rows"
done

if [ -n "$missing" ]; then
  echo "::error::These --filter clauses selected NOTHING and therefore ran on no lane:${missing}. A class was renamed, moved, or retagged — restore the name or update this lane's clause list."
  exit 1
fi

# ── Consecutive-dark history ──────────────────────────────────────────────────────────────────────────────────────
#
# The streak is read out of the predecessor runs' OWN LOGS: the census table above is printed by this same script on
# every run, so a run's log already records what each clause measured. `gh api` lists the last completed runs of this
# workflow on the streak branch and downloads each one's log archive; `unzip -p` streams it and the table rows are
# read straight back out. That needs only the GITHUB_TOKEN with `actions: read` — no new secret, no extra artifact,
# and no seeding period, because the runs that already went dark recorded their census the same way.

census_count=0
# How many predecessors were downloaded and looked at, census-bearing or not. The streak is only as trustworthy as
# the evidence under it: "streak 1" read from one predecessor that MEASURED the clause and "streak 1" read from a
# dozen runs that recorded nothing about it are the same number over opposite facts, so both counts are reported.
history_runs_seen=0
streaks_measured=0
history_blocker=""

# Every prerequisite the history read needs, named individually: "the streak could not be checked" must say WHICH
# piece is missing, or it becomes the same silent nothing this guard exists to abolish.
history_available() {
  if [ "${GITHUB_REF:-}" != "refs/heads/${DARK_STREAK_BRANCH}" ]; then
    history_blocker="this run is not on ${DARK_STREAK_BRANCH}, and a branch run has no streak to be consecutive with"
    return 1
  fi

  if ! command -v gh >/dev/null 2>&1; then history_blocker="the gh CLI is not on PATH"; return 1; fi
  if ! command -v unzip >/dev/null 2>&1; then history_blocker="unzip is not on PATH"; return 1; fi

  if [ -z "${GH_TOKEN:-${GITHUB_TOKEN:-}}" ]; then
    history_blocker="no GH_TOKEN/GITHUB_TOKEN on this step — the workflow must pass it AND grant \`actions: read\`"
    return 1
  fi

  if [ -z "${GITHUB_REPOSITORY:-}" ] || [ -z "${GITHUB_RUN_ID:-}" ] || [ -z "${GITHUB_WORKFLOW_REF:-}" ]; then
    history_blocker="this is not a GitHub Actions run (no GITHUB_REPOSITORY / GITHUB_RUN_ID / GITHUB_WORKFLOW_REF)"
    return 1
  fi

  return 0
}

# The rows of the census table this same script prints, recovered from a log: "<state> <clause>", one per line, with
# GitHub's ISO timestamp prefix stripped off the front of each log line.
census_rows() {
  sed -E 's/\r$//; s/^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z //' \
    | sed -nE 's/^[[:space:]]*(ok|UNMEASURED|MISSING)[[:space:]]+[^[:space:]]+[[:space:]]+[^[:space:]]+[[:space:]]+[^[:space:]]+[[:space:]]+([^[:space:]]+)[[:space:]]*$/\1 \2/p'
}

# What one predecessor's census recorded about one clause: UNMEASURED, MEASURED, or nothing at all.
#
# A run's census can name the same clause more than once — the archive carries EVERY lane's table, and two lanes may
# share a clause (RealModelSession runs on both the component lane and the aux whole-loop lane) — so MEASURED wins
# over UNMEASURED: one lane that measured it is enough. And absence is its own answer: a run whose census has no row
# for the clause is a run where that LANE never reported (cancelled by concurrency, red before the guard, or the
# clause not yet listed), which is evidence of NOTHING either way.
#
# A MISSING row breaks the streak exactly as an `ok` one does, and on purpose: MISSING means the clause selected no
# test at all, which exits this guard RED on the spot. The streak exists to catch a lane measuring nothing while
# reporting GREEN, and a run that went red already told a human. Read the MEASURED label below as "this predecessor
# broke the streak", not as "this predecessor took a measurement".
state_in() {
  awk -v t="$2" '$2 == t { seen++; if ($1 == "ok" || $1 == "MISSING") measured++ } END { print !seen ? "" : (measured ? "MEASURED" : "UNMEASURED") }' "$1"
}

# How many runs (this one included) recorded the clause UNMEASURED with no run in between recording it MEASURED.
# Every +1 comes from a predecessor that POSITIVELY recorded the darkness, so the count can never be inflated by a
# run that simply said nothing — those are stepped over, which is what stops one cancelled run from laundering a
# streak that is still running underneath it.
streak_of() {
  local token="$1" streak=1 census state

  for census in "$censuses"/*; do
    [ -e "$census" ] || break

    state="$(state_in "$census" "$token")"

    if [ "$state" = MEASURED ]; then break; fi
    if [ "$state" = UNMEASURED ]; then streak=$((streak + 1)); fi
  done

  printf '%s' "$streak"
}

# Stop paging the moment every unmeasured clause has had its streak broken by a run that measured it — there is
# nothing left to learn, and every further page is another log archive download.
any_clause_still_dark() {
  local token census

  for token in $unmeasured_tokens; do
    for census in "$censuses"/*; do
      [ -e "$census" ] || break
      if [ "$(state_in "$census" "$token")" = MEASURED ]; then continue 2; fi
    done

    return 0
  done

  return 1
}

collect_history() {
  local workflow runs run_id archive log

  workflow="${GITHUB_WORKFLOW_REF%%@*}"
  workflow="${workflow##*/}"

  if ! runs="$(gh api "repos/${GITHUB_REPOSITORY}/actions/workflows/${workflow}/runs?branch=${DARK_STREAK_BRANCH}&status=completed&per_page=${DARK_HISTORY_RUNS_TO_SCAN}" --jq '.workflow_runs[].id' 2>/dev/null)"; then
    history_blocker="the workflow's run history could not be listed (is \`actions: read\` granted?)"
    return 1
  fi

  archive="${work}/predecessor-logs.zip"
  log="${work}/predecessor.log"

  for run_id in $runs; do
    if [ "$run_id" = "${GITHUB_RUN_ID}" ]; then continue; fi

    history_runs_seen=$((history_runs_seen + 1))

    # `unzip -p` streams every member to stdout, so the whole run's log is read in one pass without extracting a
    # single file — which also keeps unzip away from the interactive "Continue? (y/n)" prompt a failed extraction
    # would otherwise hang this step on.
    if ! gh api "repos/${GITHUB_REPOSITORY}/actions/runs/${run_id}/logs" > "$archive" 2>/dev/null; then continue; fi
    if ! unzip -p "$archive" > "$log" 2>/dev/null; then continue; fi

    # No census header means that run's job never reached this guard. It is evidence of nothing either way, so step
    # over it and keep looking — counting it as a reset would let a single cancelled run launder the streak still
    # running underneath it.
    if ! grep -qF -- "$CENSUS_HEADER_ROW" "$log"; then continue; fi

    census_count=$((census_count + 1))
    census_rows < "$log" > "${censuses}/$(printf '%03d' "$census_count")"

    if ! any_clause_still_dark; then break; fi
  done

  # No predecessor recorded a census at all — the workflow's first run on this branch, a retention gap, or a token
  # that can list runs but not read their logs. Whatever the cause, there is nothing to be consecutive WITH, so say
  # so rather than reporting a confident streak of one.
  if [ "$census_count" -eq 0 ]; then
    history_blocker="no completed ${DARK_STREAK_BRANCH} run carried a clause census to compare against"
    return 1
  fi

  return 0
}

if [ -n "$unmeasured_tokens" ]; then
  if history_available && collect_history; then
    streaks_measured=1
  fi
fi

# ── Report ────────────────────────────────────────────────────────────────────────────────────────────────────────

if [ -n "$unmeasured" ]; then
  # NOT a failure on its own: an infra / no-credentials skip is non-gating by design. But a lane that measured
  # nothing must say so out loud instead of reporting the same green as a lane that measured everything.
  echo "::warning::These --filter clauses ran ZERO passing tests — every selected test SKIPPED, so the lane measured nothing:${unmeasured}. Check the job summary for the skip reason (no live credentials, or a gateway-infra fault)."
fi

# Only where a streak was expected: off the streak branch there is nothing to check by design, and saying so on
# every branch run would be the noise that made the warning above easy to ignore in the first place.
if [ -n "$unmeasured_tokens" ] && [ "$streaks_measured" -ne 1 ] && [ "${GITHUB_REF:-}" = "refs/heads/${DARK_STREAK_BRANCH}" ]; then
  echo "::warning::…and the consecutive-dark check could NOT run (${history_blocker}), so a clause that has measured nothing for runs on end still reads here as a one-off."
fi

# The skip reason RealModelGate recorded for this clause's first skipped test. The streak says the instrument is
# dark; this says why it went dark, which is the half an operator can act on.
skip_reason() {
  local reason

  reason="$(awk -v token="$1" '
    index($0, "<UnitTestResult") > 0 { inside = (index($0, token) > 0 && index($0, "outcome=\"NotExecuted\"") > 0) }
    index($0, "</UnitTestResult>") > 0 { inside = 0 }
    inside && match($0, /<Message>.*<\/Message>/) { print substr($0, RSTART + 9, RLENGTH - 19); exit }
  ' "$trx")"

  if [ -n "$reason" ]; then printf '%s' "$reason"; else printf '%s' "none recorded in the trx"; fi
}

# The streak column is the instrument's health trend — 0 while a clause is measuring, climbing while it is not.
write_step_summary() {
  local token state passed failed skipped

  if [ -z "${GITHUB_STEP_SUMMARY:-}" ]; then return 0; fi

  {
    printf '### Filter-clause census — `%s`\n\n' "$trx"
    printf '| clause | outcome | passed | failed | skipped | consecutive dark runs |\n'
    printf '|---|---|---|---|---|---|\n'

    while IFS='|' read -r token state passed failed skipped; do
      printf '| `%s` | %s | %s | %s | %s | %s |\n' "$token" "$state" "$passed" "$failed" "$skipped" "$(streak_cell "$token" "$state")"
    done < "$rows"

    printf '\nA clause that measures nothing reds this lane once it has done so on %s consecutive `%s` runs; a shorter streak only warns.\n' "$DARK_RUNS_TO_RED" "$DARK_STREAK_BRANCH"

    if [ "$streaks_measured" -eq 1 ]; then
      printf '\nStreaks read from %s of the %s predecessor runs scanned — the rest carried no census for the clause and are evidence of nothing either way.\n' "$census_count" "$history_runs_seen"
    fi

    printf '\n'
  } >> "$GITHUB_STEP_SUMMARY"
}

streak_cell() {
  local token="$1" state="$2" streak

  if [ "$state" != UNMEASURED ]; then printf '0'; return; fi
  if [ "$streaks_measured" -ne 1 ]; then printf 'not checked'; return; fi

  streak="$(streak_of "$token")"

  if [ "$streak" -ge "$DARK_RUNS_TO_RED" ]; then printf '**%s**' "$streak"; else printf '%s' "$streak"; fi
}

write_step_summary

persistently_dark=""
if [ "$streaks_measured" -eq 1 ]; then
  for token in $unmeasured_tokens; do
    streak="$(streak_of "$token")"

    if [ "$streak" -ge "$DARK_RUNS_TO_RED" ]; then
      persistently_dark="${persistently_dark} ${token}(${streak} runs)"
      printf '::error::%s has now measured NOTHING on %s consecutive %s runs of this workflow (the lane reds at %s; %s of the %s predecessor runs scanned carried a census). A single dark run is a by-design infra skip; a streak means this gate has been reporting green over an instrument that never ran, while still spending the lane its full live-API budget every time. Last recorded skip reason: %s. Fix the instrument, or drop the clause from this lane on purpose — the lane stays red until a run actually measures it.\n' \
        "$token" "$streak" "$DARK_STREAK_BRANCH" "$DARK_RUNS_TO_RED" "$census_count" "$history_runs_seen" "$(skip_reason "$token")"
    fi
  done
fi

if [ -n "$persistently_dark" ]; then
  echo "persistently unmeasured:${persistently_dark}"
  exit 1
fi

echo "every filter clause selected at least one test"
