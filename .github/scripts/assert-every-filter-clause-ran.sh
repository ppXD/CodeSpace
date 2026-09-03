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
# The check greps `testName`, which carries the fully-qualified name — the same string `FullyQualifiedName~` matched
# on — so the guard and the filter can never disagree about what a clause means.
#
# Usage: assert-every-filter-clause-ran.sh <trx-path> <token> [<token>...]
#   where each <token> is exactly the text after `FullyQualifiedName~` in that lane's own --filter.

set -euo pipefail

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

# One line per result: "<outcome> <fully-qualified test name>". A trx <UnitTestResult> carries both attributes on its
# opening tag, testName first, so a single pass over the element text pairs them without an XML parser.
results="$(grep -o 'testName="[^"]*"[^>]*outcome="[^"]*"' "$trx" \
  | sed -E 's/^testName="([^"]*)".*outcome="([^"]*)"$/\2 \1/' || true)"

missing=""
unmeasured=""

printf '  %-9s %-9s %-9s %-9s %s\n' outcome passed failed skipped clause
for token in "$@"; do
  matched="$(printf '%s\n' "$results" | grep -F -- "$token" || true)"
  total="$(printf '%s' "$matched" | grep -c . || true)"
  passed="$(printf '%s\n' "$matched" | grep -c '^Passed ' || true)"
  failed="$(printf '%s\n' "$matched" | grep -c '^Failed ' || true)"
  skipped="$(printf '%s\n' "$matched" | grep -c '^NotExecuted ' || true)"

  if [ "$total" -eq 0 ]; then
    printf '  %-9s %-9s %-9s %-9s %s\n' MISSING - - - "$token"
    missing="${missing} ${token}"
    continue
  fi

  if [ "$passed" -eq 0 ] && [ "$skipped" -gt 0 ]; then
    printf '  %-9s %-9s %-9s %-9s %s\n' UNMEASURED "$passed" "$failed" "$skipped" "$token"
    unmeasured="${unmeasured} ${token}(${skipped} skipped)"
    continue
  fi

  printf '  %-9s %-9s %-9s %-9s %s\n' ok "$passed" "$failed" "$skipped" "$token"
done

if [ -n "$missing" ]; then
  echo "::error::These --filter clauses selected NOTHING and therefore ran on no lane:${missing}. A class was renamed, moved, or retagged — restore the name or update this lane's clause list."
  exit 1
fi

if [ -n "$unmeasured" ]; then
  # NOT a failure: an infra / no-credentials skip is non-gating by design. But a lane that measured nothing must say
  # so out loud instead of reporting the same green as a lane that measured everything.
  echo "::warning::These --filter clauses ran ZERO passing tests — every selected test SKIPPED, so the lane measured nothing:${unmeasured}. Check the job summary for the skip reason (no live credentials, or a gateway-infra fault)."
fi

echo "every filter clause selected at least one test"
