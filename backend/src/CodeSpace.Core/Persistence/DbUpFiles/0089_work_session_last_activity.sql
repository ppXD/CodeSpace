-- 0089_work_session_last_activity.sql
--
-- The thread's most-recent activity instant — the MRU ordering key for the sessions index. Stamped at session open
-- (= creation) and bumped on every continue (a new top-level turn), both by WorkSessionService. A denormalised sort
-- key so the sessions index rides a (team_id, last_activity_at DESC, id DESC) keyset rather than a correlated
-- MAX(run.created_date) sort.
--
-- Idempotent (ADD COLUMN / CREATE INDEX IF NOT EXISTS). NOT NULL DEFAULT now() so the column lands populated for new
-- inserts; the one-time backfill then sets existing rows to the most recent of the session's creation / its latest run,
-- so pre-migration threads sort sensibly from day one.

ALTER TABLE work_session ADD COLUMN IF NOT EXISTS last_activity_at timestamptz NOT NULL DEFAULT now();

UPDATE work_session s
SET last_activity_at = GREATEST(
    s.created_date,
    COALESCE((SELECT MAX(r.created_date) FROM workflow_run r WHERE r.session_id = s.id), s.created_date));

CREATE INDEX IF NOT EXISTS idx_work_session_team_activity ON work_session (team_id, last_activity_at DESC, id DESC);
