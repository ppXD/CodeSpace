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

# Stage a fresh runner-shaped layout: a step-summary directory with per-step files, and a results directory with a
# trx. Echoes the two paths so a case can read what would have been uploaded.
stage() {
  local root; root="$(mktemp -d)"
  mkdir -p "${root}/summaries" "${root}/results"
  printf '%s\n' "$root"
}

check() {
  local mode="$1" needle="$2" file="$3" name="$4"

  if [ ! -f "$file" ]; then
    echo "  FAILED  ${name} — ${file} was never written"
    failures=$((failures + 1))
    return
  fi

  if [ "$mode" = "has" ] && grep -qF -- "$needle" "$file"; then
    echo "  ok      ${name}"
  elif [ "$mode" = "lacks" ] && ! grep -qF -- "$needle" "$file"; then
    echo "  ok      ${name}"
  else
    echo "  FAILED  ${name} — ${file} ${mode} '${needle}' was not satisfied"
    sed 's/^/          | /' "$file"
    failures=$((failures + 1))
  fi
}

# ── THE case this exists for: the configured id must not survive into the uploaded summary ───────────────────────

root="$(stage)"
printf "✅ real-model INFORMATIONAL wire — OpenAI model 'pinned-model-4-5' scored 12/14 [model fp=deadbeef (configured)]\n" > "${root}/summaries/step_summary_1"
printf "⚠️ real-model gate NON-GATING infra skip — Anthropic (pinned-model-4-5 unreachable)\n" > "${root}/summaries/step_summary_2"
CODESPACE_LLM_MODEL_ID=pinned-model-4-5 bash "$collect" "${root}/results" "${root}/summaries" >/dev/null 2>&1

check lacks "pinned-model-4-5" "${root}/results/step-summary.md" "redacts the configured model id from the collected summary"
check has "***" "${root}/results/step-summary.md" "leaves a visible *** where the id was"
check has "scored 12/14" "${root}/results/step-summary.md" "keeps the verdict itself — the artifact is still the record of what the model did"
check has "fp=deadbeef" "${root}/results/step-summary.md" "keeps the fingerprint, which is what actually travels"
check has "NON-GATING infra skip" "${root}/results/step-summary.md" "concatenates EVERY step_summary_* file, not just the first"
rm -rf "$root"

# Two occurrences on ONE line: a single-shot replacement would leave the second one behind.
root="$(stage)"
printf "model 'pinned-model-4-5' answered as pinned-model-4-5\n" > "${root}/summaries/step_summary_1"
CODESPACE_LLM_MODEL_ID=pinned-model-4-5 bash "$collect" "${root}/results" "${root}/summaries" >/dev/null 2>&1

check lacks "pinned-model-4-5" "${root}/results/step-summary.md" "redacts EVERY occurrence on a line, not just the first"
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

# ── The trx is uploaded too, and a failing blessed wire writes its verdict into the assertion message ────────────

root="$(stage)"
printf '<UnitTestResult outcome="Failed"><Message>REQUIRED wire — Anthropic model %s missed</Message></UnitTestResult>\n' "pinned-model-4-5" > "${root}/results/real-model.trx"
printf 'nothing here\n' > "${root}/summaries/step_summary_1"
CODESPACE_LLM_MODEL_ID=pinned-model-4-5 bash "$collect" "${root}/results" "${root}/summaries" >/dev/null 2>&1

check lacks "pinned-model-4-5" "${root}/results/real-model.trx" "redacts the configured model id from the trx, in place"
check has "REQUIRED wire" "${root}/results/real-model.trx" "leaves the rest of the trx intact"
rm -rf "$root"

# ── Best-effort: never a crash, never a mangled artifact, whatever the runner hands it ──────────────────────────

# No secret configured (a fork, a local run) → the content passes through untouched rather than being blanked.
root="$(stage)"
printf "OpenAI scored 12/14\n" > "${root}/summaries/step_summary_1"
CODESPACE_LLM_MODEL_ID= bash "$collect" "${root}/results" "${root}/summaries" >/dev/null 2>&1

check has "OpenAI scored 12/14" "${root}/results/step-summary.md" "passes content through unchanged when no id is configured"
rm -rf "$root"

# No step_summary_* files at all (the runner layout changed) → an empty collected file, and still exit 0.
root="$(stage)"
CODESPACE_LLM_MODEL_ID=pinned-model-4-5 bash "$collect" "${root}/results" "${root}/summaries" >/dev/null 2>&1
got=$?

if [ "$got" -eq 0 ] && [ -f "${root}/results/step-summary.md" ] && [ ! -s "${root}/results/step-summary.md" ]; then
  echo "  ok      writes an EMPTY summary and exits 0 when the runner has no step_summary_* files"
else
  echo "  FAILED  writes an EMPTY summary and exits 0 when the runner has no step_summary_* files — exit ${got}"
  failures=$((failures + 1))
fi
rm -rf "$root"

# A results directory that does not exist yet is created, not fatal.
root="$(stage)"
printf "OpenAI model 'pinned-model-4-5' scored 12/14\n" > "${root}/summaries/step_summary_1"
CODESPACE_LLM_MODEL_ID=pinned-model-4-5 bash "$collect" "${root}/absent/results" "${root}/summaries" >/dev/null 2>&1

check lacks "pinned-model-4-5" "${root}/absent/results/step-summary.md" "creates a missing results directory and still redacts"
rm -rf "$root"

if [ "$failures" -ne 0 ]; then
  echo "${failures} redaction self-test(s) failed"
  exit 1
fi

echo "redaction self-tests passed"
