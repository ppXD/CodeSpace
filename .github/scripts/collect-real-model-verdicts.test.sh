#!/usr/bin/env bash
#
# A redaction that silently fails open is worse than no redaction: the artifact still ships the secret, and the step
# name says it was handled. So every case below asserts on the CONTENT that would be uploaded — the collected
# step-summary and the trx — never on the exit code, which is 0 by design.
#
# Run: bash .github/scripts/collect-real-model-verdicts.test.sh

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
collect="${here}/collect-real-model-verdicts.sh"

failures=0

pass() { echo "  ok      $1"; }
fail() { echo "  FAILED  $1"; failures=$((failures + 1)); }

# Stage a fresh runner-shaped layout: a step-summary directory with per-step files, and a results directory.
stage() {
  local root; root="$(mktemp -d)"
  mkdir -p "${root}/summaries" "${root}/results"
  printf '%s\n' "$root"
}

check() {
  local mode="$1" needle="$2" file="$3" name="$4"

  if [ ! -f "$file" ]; then
    fail "${name} — ${file} was never written"
    return
  fi

  if [ "$mode" = "has" ] && grep -qF -- "$needle" "$file"; then
    pass "$name"
  elif [ "$mode" = "lacks" ] && ! grep -qF -- "$needle" "$file"; then
    pass "$name"
  else
    fail "${name} — ${file} ${mode} '${needle}' was not satisfied"
    sed 's/^/          | /' "$file"
  fi
}

check_output() {
  local mode="$1" needle="$2" out="$3" name="$4"

  if [ "$mode" = "has" ] && printf '%s' "$out" | grep -qF -- "$needle"; then
    pass "$name"
  elif [ "$mode" = "lacks" ] && ! printf '%s' "$out" | grep -qF -- "$needle"; then
    pass "$name"
  else
    fail "${name} — the step output ${mode} '${needle}' was not satisfied"
    printf '%s\n' "$out" | sed 's/^/          | /'
  fi
}

# The values the fixtures pretend are secrets. The base URL is the shape that broke a sed-based redaction: it
# carries `:` and `/`.
MODEL_ID="pinned-model-4-5"
BASE_URL="https://gateway.internal.example.com:8443/v1/openai"
API_KEY="sk-codespace-0123456789abcdef"
SUITE_URL="https://hidden-suite.example.com/qualification.json"
OBSERVED="ZhipuAI/GLM-5.3-Flash"
# A model name NO side file ever registered — the real-model-footer-signals lane never calls the gate's ObserveModel,
# so run 33814929951's trx named its live model with no needle to strike it by. URL-shaped on purpose: ':' and '/'
# are exactly what a value-based redaction has to escape, and the positional pass must not care.
# The host is deliberately unrelated to BASE_URL, so nothing on the needle list can strike it by value — the ONLY
# thing that can mask it is the positional pass.
UNOBSERVED="https://models.private-vendor.example:8443/v1/glm-5.3-flash"

# Run the collect step with every secret present.
run_collect() {
  CODESPACE_LLM_MODEL_ID="$MODEL_ID" \
  CODESPACE_LLM_BASE_URL="$BASE_URL" \
  CODESPACE_LLM_API_KEY="$API_KEY" \
  CODESPACE_HIDDEN_SUITE_URL="$SUITE_URL" \
    bash "$collect" "$@" 2>&1
}

# ── The list of secret names is the contract with the workflow ───────────────────────────────────────────────────
#
# A secret renamed in real-model.yml but not here stops being redacted, silently. Pin each name literally so the
# rename is a visible decision, and cross-check that the workflow passes exactly these.

for var in CODESPACE_LLM_MODEL_ID CODESPACE_LLM_BASE_URL CODESPACE_LLM_API_KEY CODESPACE_HIDDEN_SUITE_URL; do
  if grep -qF -- "$var" "$collect"; then
    pass "the redaction list names ${var}"
  else
    fail "the redaction list names ${var}"
  fi
done

workflow="${here}/../workflows/real-model.yml"
if [ -f "$workflow" ]; then
  missing=""
  for var in $(grep -o 'secrets\.[A-Z_0-9]*' "$workflow" | sed 's/secrets\.//' | sort -u); do
    grep -qF -- "$var" "$collect" || missing="${missing} ${var}"
  done

  if [ -z "$missing" ]; then
    pass "every secret real-model.yml passes is on the redaction list"
  else
    fail "every secret real-model.yml passes is on the redaction list — unredacted:${missing}"
  fi
fi

# ── THE case this exists for: no gateway secret survives into an uploaded file ───────────────────────────────────

root="$(stage)"
printf "✅ real-model INFORMATIONAL wire — OpenAI model '%s' scored 12/14 [model fp=deadbeef (configured)]\n" "$MODEL_ID" > "${root}/summaries/step_summary_1"
printf "⚠️ real-model gate NON-GATING infra skip — Anthropic: HttpRequestException reaching %s\n" "$BASE_URL" > "${root}/summaries/step_summary_2"
printf "[realmodel] key=%s suite=%s\n" "$API_KEY" "$SUITE_URL" > "${root}/summaries/step_summary_3"
# The trx shape that actually leaked on run 33754366815: HttpClient's own Serilog lines, captured as test stdout,
# naming the gateway 302 times — plus the provider model name from LlmCompleteNode's completion line.
{
  printf '<StdOut>Start processing HTTP request POST %s/messages</StdOut>\n' "$BASE_URL"
  printf '<StdOut>LLM completion %s in=120 out=45 finish=stop</StdOut>\n' "$OBSERVED"
  # The footer-signals shape: a lane that never registered its model, so no needle exists for it. Two variants —
  # one followed by the usual fields, one where the name ENDS the element and the closing tag must survive.
  printf '<StdOut>[.. INF] LLM completion %s in=0 out=1427 finish=end_turn</StdOut>\n' "$UNOBSERVED"
  printf '<StdOut>LLM completion %s</StdOut>\n' "$UNOBSERVED"
  printf '<Message>REQUIRED wire - Anthropic model %s missed [key %s]</Message>\n' "$MODEL_ID" "$API_KEY"
} > "${root}/results/real-model.trx"
printf '%s\n' "$OBSERVED" > "${root}/summaries/codespace_observed_models"
run_collect "${root}/results" "${root}/summaries" >/dev/null

check lacks "$MODEL_ID"  "${root}/results/step-summary.md" "redacts the configured model id"
check lacks "$BASE_URL"  "${root}/results/step-summary.md" "redacts the gateway base URL, ':' and '/' and all"
check lacks "gateway.internal.example.com" "${root}/results/step-summary.md" "redacts the gateway HOST, not just the scheme"
check lacks "$API_KEY"   "${root}/results/step-summary.md" "redacts the gateway API key"
check lacks "$SUITE_URL" "${root}/results/step-summary.md" "redacts the hidden-suite URL"
check lacks "$OBSERVED"  "${root}/results/real-model.trx" "redacts an OBSERVED model name out of the trx, from the gate's side file"
check has   "LLM completion ***" "${root}/results/real-model.trx" "leaves the surrounding log line intact around the struck name"
check lacks "$UNOBSERVED" "${root}/results/real-model.trx" "masks a model name NO side file registered — by POSITION after 'LLM completion ', ':' and '/' and all"
check lacks "private-vendor.example" "${root}/results/real-model.trx" "masks the whole unobserved name, not a prefix of it"
check has   "LLM completion *** in=0 out=1427 finish=end_turn" "${root}/results/real-model.trx" "keeps the token counts and the finish reason — the only reason the line is worth uploading"
check has   "LLM completion ***</StdOut>" "${root}/results/real-model.trx" "stops the mask at the closing tag when the name ends the element, rather than eating the XML"
check lacks "$BASE_URL"  "${root}/results/real-model.trx" "redacts the gateway URL - ':' and '/' and all - out of the TRX, not just the summary"
check lacks "gateway.internal.example.com" "${root}/results/real-model.trx" "redacts the gateway HOST out of the trx"
check has   "Start processing HTTP request POST ***" "${root}/results/real-model.trx" "leaves the HttpClient log line readable around the struck URL"
check lacks "$MODEL_ID" "${root}/results/real-model.trx" "redacts the configured model id out of the trx assertion message"
check lacks "$API_KEY"  "${root}/results/real-model.trx" "redacts the API key out of the trx"
check has   "REQUIRED wire" "${root}/results/real-model.trx" "leaves the verdict itself in the trx"
check has   "scored 12/14" "${root}/results/step-summary.md" "keeps the verdict itself — the artifact is still the record of what the model did"
check has   "fp=deadbeef"  "${root}/results/step-summary.md" "keeps the fingerprint, which is what actually travels"
check has   "infra skip"   "${root}/results/step-summary.md" "concatenates EVERY step_summary_* file, not just the first"
rm -rf "$root"

# Two occurrences on ONE line: a single-shot replacement would leave the second one behind.
root="$(stage)"
printf "model '%s' answered as %s\n" "$MODEL_ID" "$MODEL_ID" > "${root}/summaries/step_summary_1"
run_collect "${root}/results" "${root}/summaries" >/dev/null

check lacks "$MODEL_ID" "${root}/results/step-summary.md" "redacts EVERY occurrence on a line, not just the first"
rm -rf "$root"

# A model id routinely carries regex metacharacters. A sed-based redaction would treat these as a pattern and miss
# the literal text — failing OPEN, which is the whole disease this guard exists to prevent.
root="$(stage)"
printf "model 'openai/gpt-4.1[2026-08-30]' scored 14/14\n" > "${root}/summaries/step_summary_1"
CODESPACE_LLM_MODEL_ID='openai/gpt-4.1[2026-08-30]' bash "$collect" "${root}/results" "${root}/summaries" >/dev/null 2>&1

check lacks "openai/gpt-4.1[2026-08-30]" "${root}/results/step-summary.md" "redacts an id containing regex metacharacters LITERALLY"
# ...and the surrounding verdict SURVIVES: a redaction that blows up and fails closed also satisfies the check
# above, so without this the metacharacter case would pass on an empty artifact.
check has "scored 14/14" "${root}/results/step-summary.md" "redacts a metacharacter-bearing id WITHOUT discarding the verdict"
rm -rf "$root"

# ── A missing needle must be LOUD: an unredacted secret under a step named "redact" is the worst outcome ─────────

root="$(stage)"
printf "OpenAI scored 12/14\n" > "${root}/summaries/step_summary_1"
out="$(CODESPACE_LLM_MODEL_ID="$MODEL_ID" CODESPACE_LLM_BASE_URL= CODESPACE_LLM_API_KEY= CODESPACE_HIDDEN_SUITE_URL= bash "$collect" "${root}/results" "${root}/summaries" 2>&1)"

check_output has "::warning::" "$out" "warns when a listed secret env var is EMPTY"
check_output has "CODESPACE_LLM_BASE_URL is EMPTY" "$out" "names WHICH secret could not be redacted"
check_output lacks "CODESPACE_LLM_MODEL_ID is EMPTY" "$out" "does not warn about a secret that WAS present"
rm -rf "$root"

# A short value is a fragment, not an identifier: striking "1" would shred every line. Refuse it, loudly, and leave
# the content alone rather than mangling the artifact.
root="$(stage)"
printf "attempt 1 of 3 scored 12/14\n" > "${root}/summaries/step_summary_1"
out="$(CODESPACE_LLM_MODEL_ID=1 CODESPACE_LLM_BASE_URL= CODESPACE_LLM_API_KEY= CODESPACE_HIDDEN_SUITE_URL= bash "$collect" "${root}/results" "${root}/summaries" 2>&1)"

check_output has "shorter than 8 characters" "$out" "refuses a too-short needle LOUDLY"
check has "attempt 1 of 3 scored 12/14" "${root}/results/step-summary.md" "leaves the artifact unmangled when a needle is refused"
rm -rf "$root"

# Every secret absent (a fork, a local run) → content passes through untouched rather than being blanked.
root="$(stage)"
printf "OpenAI scored 12/14\n" > "${root}/summaries/step_summary_1"
CODESPACE_LLM_MODEL_ID= CODESPACE_LLM_BASE_URL= CODESPACE_LLM_API_KEY= CODESPACE_HIDDEN_SUITE_URL= bash "$collect" "${root}/results" "${root}/summaries" >/dev/null 2>&1

check has "OpenAI scored 12/14" "${root}/results/step-summary.md" "passes content through unchanged when no secret is configured"
rm -rf "$root"

# The positional model mask does NOT depend on any needle being configured — a fork with no secrets set still must
# not upload a live model name — and it must not touch text that merely mentions the phrase.
root="$(stage)"
{
  printf 'LLM completion %s in=7 out=9 finish=stop\n' "$UNOBSERVED"
  printf 'ran the LLM completion node twice\n'
} > "${root}/summaries/step_summary_1"
CODESPACE_LLM_MODEL_ID= CODESPACE_LLM_BASE_URL= CODESPACE_LLM_API_KEY= CODESPACE_HIDDEN_SUITE_URL= bash "$collect" "${root}/results" "${root}/summaries" >/dev/null 2>&1

check lacks "$UNOBSERVED" "${root}/results/step-summary.md" "masks the completion model even with NO secret configured — the mask is positional, not needle-driven"
check has "LLM completion *** in=7 out=9 finish=stop" "${root}/results/step-summary.md" "keeps the rest of the completion line"
# The mask is positional and unconditional, so prose that happens to use the phrase loses its next word too. That is
# the DELIBERATE direction to fail in: the alternative is matching the full Serilog line shape, which stops masking
# the moment someone edits the template — and a redaction that quietly stops working is the disease this file exists
# to prevent. Pinned so the tradeoff is a visible decision rather than a surprise in an artifact.
check has "ran the LLM completion *** twice" "${root}/results/step-summary.md" "masks unconditionally — prose after the phrase is collateral, and failing CLOSED is the right direction"
rm -rf "$root"

# ── Best-effort: never a crash, never a mangled artifact, whatever the runner hands it ──────────────────────────

# No step_summary_* files at all (the runner layout changed) → an empty collected file, and still exit 0.
root="$(stage)"
run_collect "${root}/results" "${root}/summaries" >/dev/null
got=$?

if [ "$got" -eq 0 ] && [ -f "${root}/results/step-summary.md" ] && [ ! -s "${root}/results/step-summary.md" ]; then
  pass "writes an EMPTY summary and exits 0 when the runner has no step_summary_* files"
else
  fail "writes an EMPTY summary and exits 0 when the runner has no step_summary_* files — exit ${got}"
fi
rm -rf "$root"

# A results directory that does not exist yet is created, not fatal.
root="$(stage)"
printf "OpenAI model '%s' scored 12/14\n" "$MODEL_ID" > "${root}/summaries/step_summary_1"
run_collect "${root}/absent/results" "${root}/summaries" >/dev/null

check lacks "$MODEL_ID" "${root}/absent/results/step-summary.md" "creates a missing results directory and still redacts"
rm -rf "$root"

if [ "$failures" -ne 0 ]; then
  echo "${failures} redaction self-test(s) failed"
  exit 1
fi

echo "redaction self-tests passed"
