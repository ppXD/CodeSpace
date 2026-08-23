-- 0165_workflow_run_wait_pending_created_index.sql
--
-- The Suspended run UI polls one newest pending wait. Historical resolved waits can be numerous for a long-lived map,
-- but they can never satisfy that observation. Keep the polling index partial and in exact LIMIT 1 order so PostgreSQL
-- touches one live row without scanning resolved history or sorting payload-bearing tuples.

CREATE INDEX IF NOT EXISTS idx_workflow_run_wait_pending_created
    ON workflow_run_wait (run_id, created_at DESC, id DESC)
    WHERE status = 'Pending';

COMMENT ON INDEX idx_workflow_run_wait_pending_created IS
    'Newest pending wait for bounded Suspended-run observation; excludes resolved history.';
