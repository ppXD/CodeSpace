-- 0072_work_session_turn_counter.sql
--
-- The atomic per-session turn counter — the race-free replacement for ContinueAsync's old MAX(session_turn_index)+1
-- read. That read ran under READ COMMITTED with no lock, so two concurrent continues of the SAME session could read
-- the same MAX and assign the SAME ordinal (a silent duplicate top-level turn). ContinueAsync now does
--   UPDATE work_session SET last_turn_index = last_turn_index + 1 WHERE id = X AND ... RETURNING last_turn_index
-- which row-locks the session, so concurrent continues SERIALISE and each gets a distinct ordinal — no duplicate by
-- CONSTRUCTION (no unique constraint, no insert-time exception, no deploy-time backfill-collision risk).
--
-- last_turn_index = the highest top-level turn ordinal assigned. New sessions default to 1 (the opening run's turn).
-- Backfill existing sessions to their current highest turn (a session always has its opening turn 1, so >= 1).
-- Idempotent: ADD COLUMN IF NOT EXISTS + an idempotent backfill (DbUp journals the script, and re-running the UPDATE
-- would re-derive the same MAX).

ALTER TABLE work_session ADD COLUMN IF NOT EXISTS last_turn_index integer NOT NULL DEFAULT 1;

UPDATE work_session ws
SET last_turn_index = GREATEST(1, COALESCE((SELECT MAX(wr.session_turn_index) FROM workflow_run wr WHERE wr.session_id = ws.id), 1));
