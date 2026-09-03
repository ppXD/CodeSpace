#!/usr/bin/env bash
#
# Collect the real-model verdicts for upload, with the configured model id REDACTED.
#
# The trx + the step-summary verdicts are the only record of WHAT the live model actually did, so they are uploaded
# as artifacts. GitHub masks a secret in the LOG, never in a FILE — and run 33723910434's `real-model-results`
# artifact shipped the raw CODESPACE_LLM_MODEL_ID (a repository secret) inside its step-summary copy, readable by
# anyone who could download it. The gate's own stamp no longer names a model, but a dozen per-lane verdict strings
# still interpolate it ("Anthropic model '<id>' scored 12/14"), so this is the defence in depth that covers them all:
# every occurrence of the configured id is replaced with *** before anything leaves the runner.
#
# A step summary is a per-STEP file, so this later step cannot read the test step's `$GITHUB_STEP_SUMMARY` directly —
# it concatenates every step_summary_* file in that directory instead. Best-effort by design: if the runner layout
# ever changes the collected file is simply empty, and nothing about the job's result changes. It NEVER exits
# non-zero — a collection detail must not red a lane whose tests passed.
#
# Usage: collect-real-model-verdicts.sh <results-dir> [<step-summary-dir>]
#   <results-dir>       where step-summary.md is written and *.trx are redacted in place (e.g. backend/TestResults)
#   <step-summary-dir>  where the step_summary_* files live (default: the directory of $GITHUB_STEP_SUMMARY)

set -uo pipefail

results="${1:-}"
if [ -z "$results" ]; then
  echo "::error::collect-real-model-verdicts.sh needs a results directory"
  exit 1
fi

summaries="${2:-$(dirname "${GITHUB_STEP_SUMMARY:-/nonexistent/step_summary}")}"
secret="${CODESPACE_LLM_MODEL_ID:-}"

mkdir -p "$results"

# Literal (NOT regex) replacement of the secret. sed would have to escape whatever the id happens to contain — a
# model id carries dots and slashes routinely — and a mis-escaped pattern fails OPEN, leaving the secret in place.
redact() {
  awk -v needle="$secret" '
    needle == "" { print; next }
    {
      line = $0; out = ""
      while ((p = index(line, needle)) > 0) {
        out = out substr(line, 1, p - 1) "***"
        line = substr(line, p + length(needle))
      }
      print out line
    }'
}

scratch="$(mktemp)"
trap 'rm -f "$scratch"' EXIT

# The step summaries → one collected file. Fail CLOSED: if the redaction breaks, ship an EMPTY file rather than an
# unredacted one.
cat "$summaries"/step_summary_* > "$scratch" 2>/dev/null || true
redact < "$scratch" > "$results/step-summary.md" || : > "$results/step-summary.md"

# The trx files, redacted in place. A failing blessed wire puts its verdict in the assertion message, so the same id
# lands here too.
for trx in "$results"/*.trx; do
  [ -f "$trx" ] || continue

  if redact < "$trx" > "$scratch"; then
    cat "$scratch" > "$trx"
  else
    echo "::warning::could not redact ${trx} — it may still name the configured model id"
  fi
done

exit 0
