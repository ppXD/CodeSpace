-- 0162_workflow_run_session_run_number_index.sql
--
-- Additive observation-only access path for bounded Work Session membership pages. RunNumber is immutable after the
-- database assigns it at INSERT, so (session_id, run_number) is a stable keyset. The page's head freezes only which
-- run identities are admitted; mutable status/error/timing still require a revision boundary for cross-page snapshots.
-- No execution, terminal, completion, planner, or harness path consumes this index.

CREATE INDEX IF NOT EXISTS idx_workflow_run_session_run_number
    ON workflow_run (session_id, run_number)
    WHERE session_id IS NOT NULL;

COMMENT ON INDEX idx_workflow_run_session_run_number IS
    'Bounded Work Session membership keyset by immutable RunNumber; does not snapshot mutable run state.';
