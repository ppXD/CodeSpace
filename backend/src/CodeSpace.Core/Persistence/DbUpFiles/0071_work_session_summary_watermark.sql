-- 0071_work_session_summary_watermark.sql
--
-- Rolling session summary (long-thread memory): the `work_session.summary` column (reserved since 0069) now holds the
-- LLM-distilled context of OLDER turns — those scrolled out of the recent verbatim window the context-builder renders.
-- This adds the WATERMARK the summarizer needs: the highest workflow_run.session_turn_index the summary already covers,
-- so distillation is INCREMENTAL (fold only newly-scrolled-out turns), never a full re-summarize.
--
-- Pure additive, idempotent (mirrors 0066/0069): one NULLABLE column. NULL = no summary yet (a short thread that never
-- exceeded the recent window carries no summary + no watermark — byte-identical to the pre-summary digest).

ALTER TABLE work_session
    ADD COLUMN IF NOT EXISTS summary_through_turn_index INTEGER NULL;

COMMENT ON COLUMN work_session.summary_through_turn_index IS
    'Watermark for work_session.summary: the highest workflow_run.session_turn_index the rolling summary covers. The '
    'summarizer folds only turns above this (newly scrolled out of the recent window) into the summary, then advances '
    'it. NULL = no summary yet.';
