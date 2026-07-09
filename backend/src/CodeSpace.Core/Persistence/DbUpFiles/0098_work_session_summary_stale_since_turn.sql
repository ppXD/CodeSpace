-- 0098_work_session_summary_stale_since_turn.sql
--
-- S4 fold: SessionSummarizer's distillation is fail-open (no pool model / a credential-decrypt or LLM error leaves
-- Summary unchanged) — previously silent. `work_session.summary_stale_since_turn` records the oldest turn a
-- distillation attempt failed to fold, so the digest/room can surface "this summary may be incomplete" instead of
-- quietly showing a stale rolling summary as if it were current. NULL = fully caught up (the common case).

ALTER TABLE work_session ADD COLUMN IF NOT EXISTS summary_stale_since_turn integer NULL;
