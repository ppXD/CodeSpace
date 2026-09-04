#!/usr/bin/env bash
#
# Collect the real-model verdicts for upload, with every gateway secret REDACTED.
#
# The trx + the step-summary verdicts are the only record of WHAT the live model actually did, so they are uploaded
# as artifacts. GitHub masks a secret in the LOG, never in a FILE — and the artifacts have shipped secrets twice
# already: run 33723910434's step-summary copy carried the raw CODESPACE_LLM_MODEL_ID, and run 33754366815's
# real-model-footer-signals.trx carried the gateway BASE_URL plus a provider model name, captured out of the test
# process's console into <StdOut>.
#
# So the redaction covers BOTH kinds of needle:
#   * every secret this workflow passes to the lanes, by NAME (SECRET_VARS below) — the ids, hosts and keys;
#   * every model name a live response actually reported, read out of the side file RealModelGate writes beside the
#     step summaries (it knows the values; matching on log-message SHAPE would break the moment a template changed).
#
# A step summary is a per-STEP file, so this later step cannot read the test step's `$GITHUB_STEP_SUMMARY` directly —
# it concatenates every step_summary_* file in that directory instead. Best-effort by design: if the runner layout
# ever changes the collected file is simply empty, and nothing about the job's result changes. It NEVER exits
# non-zero — a collection detail must not red a lane whose tests passed — but it does WARN loudly when a needle it
# expected is missing, because a redaction that silently no-ops is worse than none: the artifact still carries the
# secret, under a step name that claims otherwise.
#
# Usage: collect-real-model-verdicts.sh <results-dir> [<step-summary-dir>]
#   <results-dir>       where step-summary.md is written and *.trx are redacted in place (e.g. backend/TestResults)
#   <step-summary-dir>  where the step_summary_* files live (default: the directory of $GITHUB_STEP_SUMMARY)

set -uo pipefail

# Every secret the real-model workflow puts in a job env. Renaming one here without renaming it in the workflow
# silently stops redacting it, so the self-test pins this list literally.
SECRET_VARS="CODESPACE_LLM_MODEL_ID CODESPACE_LLM_BASE_URL CODESPACE_LLM_API_KEY CODESPACE_HIDDEN_SUITE_URL"

# A needle this short is not an identifier, it is a fragment — striking it would shred unrelated text (a numeric
# knob value like "1" would hit every line). Refuse it loudly instead of mangling the artifact.
MIN_NEEDLE_LENGTH=8

# The side file RealModelGate appends provider-reported model names to (RealModelGate.ObservedModelsFileName).
OBSERVED_MODELS_FILE="codespace_observed_models"

# LlmCompleteNode logs `"LLM completion {Model} in={InTok} out={OutTok} finish={Finish}"` (LlmCompleteNode.cs), and
# xUnit captures that into <StdOut>. Every lane that reaches the gate registers its provider-reported name in the
# side file above — but a lane that never calls ObserveModel (real-model-footer-signals is one; run 33814929951's
# trx named a live model on line 249) has no needle to strike, so the name shipped in the clear.
#
# So the model token is masked by POSITION as well as by value: whatever follows "LLM completion " on a line is the
# provider's model name by construction, whether or not this job ever learned it. Only the VALUE is struck; the
# template's own words and the in=/out=/finish= fields stay readable, because the count and the finish reason are
# what makes the line worth uploading at all.
LLM_COMPLETION_PREFIX="LLM completion "

results="${1:-}"
if [ -z "$results" ]; then
  echo "::error::collect-real-model-verdicts.sh needs a results directory"
  exit 1
fi

summaries="${2:-$(dirname "${GITHUB_STEP_SUMMARY:-/nonexistent/step_summary}")}"

mkdir -p "$results"

needles="$(mktemp)"
scratch="$(mktemp)"
trap 'rm -f "$needles" "$scratch"' EXIT

# ── Collect the needles ─────────────────────────────────────────────────────────────────────────────────────────

add_needle() {
  local value="$1" origin="$2"

  if [ "${#value}" -lt "$MIN_NEEDLE_LENGTH" ]; then
    echo "::warning::${origin} is shorter than ${MIN_NEEDLE_LENGTH} characters — NOT redacting it, because striking so short a literal would shred unrelated text in the artifacts. Check it by hand."
    return
  fi

  printf '%s\n' "$value" >> "$needles"
}

for var in $SECRET_VARS; do
  value="${!var:-}"

  if [ -z "$value" ]; then
    echo "::warning::${var} is EMPTY in this job, so nothing can be redacted for it. If the secret was renamed or unset, this step is a no-op under a name that says otherwise — and any artifact naming that value ships it in the clear."
    continue
  fi

  add_needle "$value" "$var"
done

if [ -f "${summaries}/${OBSERVED_MODELS_FILE}" ]; then
  while IFS= read -r observed; do
    [ -n "$observed" ] && add_needle "$observed" "observed model name"
  done < "${summaries}/${OBSERVED_MODELS_FILE}"
fi

sort -u "$needles" -o "$needles" 2>/dev/null || true

# ── Redact ─────────────────────────────────────────────────────────────────────────────────────────────────────

# Literal (NOT regex) replacement of every needle. sed would have to escape whatever the values happen to contain —
# a base URL carries `:` and `/`, a model id carries dots and slashes — and a mis-escaped pattern fails OPEN,
# leaving the secret in place.
redact() {
  awk -v needlefile="$needles" '
    BEGIN {
      count = 0
      while ((getline needle < needlefile) > 0)
        if (length(needle) > 0) needles[++count] = needle
    }
    {
      line = $0
      for (i = 1; i <= count; i++) {
        out = ""
        while ((p = index(line, needles[i])) > 0) {
          out = out substr(line, 1, p - 1) "***"
          line = substr(line, p + length(needles[i]))
        }
        line = out line
      }
      print line
    }' | mask_completion_models
}

# The positional pass. A model name is a bare token — it never contains a space, and in a trx it is bounded by the
# surrounding XML/quoting — so the value is the run of characters after the prefix up to the first space, '<' or '"'.
# Stopping at '<' matters: a completion line at the very end of a <StdOut> element would otherwise swallow the
# closing tag into the mask and corrupt the XML the artifact is supposed to remain.
mask_completion_models() {
  awk -v prefix="$LLM_COMPLETION_PREFIX" '
    {
      line = $0
      out = ""
      while ((p = index(line, prefix)) > 0) {
        out = out substr(line, 1, p + length(prefix) - 1)
        line = substr(line, p + length(prefix))

        if (match(line, /^[^ <"]+/) > 0) {
          out = out "***"
          line = substr(line, RSTART + RLENGTH)
        }
      }
      print out line
    }'
}

# The step summaries → one collected file. Fail CLOSED: if the redaction breaks, ship an EMPTY file rather than an
# unredacted one.
cat "$summaries"/step_summary_* > "$scratch" 2>/dev/null || true
redact < "$scratch" > "$results/step-summary.md" || : > "$results/step-summary.md"

# The trx files, redacted in place. A failing blessed wire puts its verdict in the assertion message, and xUnit
# captures the test process's console into <StdOut>, so the same values land here too.
for trx in "$results"/*.trx; do
  [ -f "$trx" ] || continue

  if redact < "$trx" > "$scratch"; then
    cat "$scratch" > "$trx"
  else
    echo "::warning::could not redact ${trx} — it may still carry a gateway secret"
  fi
done

exit 0
