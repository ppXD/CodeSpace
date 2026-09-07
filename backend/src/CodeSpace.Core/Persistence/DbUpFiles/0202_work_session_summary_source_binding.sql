-- 0202_work_session_summary_source_binding.sql
--
-- P17 (durable context): a persisted rolling summary (work_session.summary) previously carried no pointer back to the
-- source rows it distilled — a turn's persisted unresolved CompletionAssessmentRecord vanished from continuation
-- context once its turn scrolled past the recent verbatim window, and an already-folded turn's summary could not
-- detect its bound source (a rerun's new effective attempt, a mutated result, a newly recorded assessment) changing
-- behind an unmoved watermark. `summary_source_binding_jsonb` is a JSON array of per-folded-turn source bindings
-- (turn, effective run id, a content fingerprint, latest assessment id) SessionSummarizer writes and
-- SessionContextBuilder reads to recover carried-forward evidence without re-deriving or losing it.
--
-- Pure additive, idempotent (mirrors 0071/0098): one NULLABLE column. NULL = no binding recorded yet (a thread that
-- never folded a turn, or a legacy row from before this column existed — explicit unknown provenance, never a
-- fabricated source hash).

ALTER TABLE work_session
    ADD COLUMN IF NOT EXISTS summary_source_binding_jsonb JSONB NULL;

COMMENT ON COLUMN work_session.summary_source_binding_jsonb IS
    'Durable per-folded-turn source binding for work_session.summary: a JSON array of {turn, effectiveRunId, '
    'resultFingerprint, assessmentId}. SessionSummarizer writes one entry per folded turn and re-checks every '
    'existing entry against the CURRENT effective source on each run, refreshing the summary when one changed '
    'behind the watermark. SessionContextBuilder reads it to carry an out-of-window turn''s unresolved '
    'CompletionAssessmentRecord into the digest. NULL = no binding recorded yet (legacy or never-folded row).';
