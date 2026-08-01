#!/usr/bin/env bash
#
# Fail unless EVERY `FullyQualifiedName~<token>` clause of a lane's --filter actually selected a test.
#
# Why this exists, twice over: `dotnet test --filter` exits 0 when the filter matches ZERO tests, so a renamed or
# moved class silently stops running and its lane stays green. The previous guard counted total executed tests and
# required >= 1 — which cannot see the real failure mode. A lane selecting four classes that drops to three still
# reports a positive count, and the two times this actually happened (the session evals, then the Custom-provider
# gate) both had a lane whose OTHER clauses kept the count healthy while the dropped one ran nowhere at all.
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

missing=""
for token in "$@"; do
  if grep -qE "testName=\"[^\"]*${token}" "$trx"; then
    echo "  ok      ${token}"
  else
    echo "  MISSING ${token}"
    missing="${missing} ${token}"
  fi
done

if [ -n "$missing" ]; then
  echo "::error::These --filter clauses selected NOTHING and therefore ran on no lane:${missing}. A class was renamed, moved, or retagged — restore the name or update this lane's clause list."
  exit 1
fi

echo "every filter clause selected at least one test"
