-- 0070_workflow_run_session_index.sql
--
-- The WorkSession timeline index, deferred from 0069 (S1) until the query that needs it landed. S4's session-context
-- builder reads a thread's prior turns on every CONTINUE — "the runs of session X, by turn" — so that access path is
-- now hot and gets its own index.
--
-- PARTIAL on session_id IS NOT NULL: the vast majority of runs are session-less (null), so the index stays tiny while
-- session adoption is sparse. (session_id, session_turn_index) covers both the lookup (WHERE session_id = X) and the
-- ordering / MAX(session_turn_index) the continue + context paths use. Idempotent (IF NOT EXISTS).

CREATE INDEX IF NOT EXISTS idx_workflow_run_session
    ON workflow_run(session_id, session_turn_index)
    WHERE session_id IS NOT NULL;
