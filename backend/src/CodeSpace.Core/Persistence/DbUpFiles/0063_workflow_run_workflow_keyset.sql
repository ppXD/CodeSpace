-- 0063_workflow_run_workflow_keyset.sql
--
-- A workflow-filtered runs view (the generic runs index with ?workflowId=) ordered newest-first now needs a keyset
-- index whose order matches the query's ORDER BY (created_date DESC, id DESC). The pre-existing
-- idx_workflow_run_by_workflow_started (0009) orders by started_at — which is re-stamped on every resume — so it
-- cannot drive a created_date keyset without a sort. Add the matching composite.
--
-- Partial on (workflow_id IS NOT NULL AND source_type <> 'workflow.child') so it mirrors the query exactly: snapshot
-- / task runs (null workflow_id) are never workflow-filtered, and the index excludes child runs the same way the
-- team index does — so a `WHERE workflow_id = ? AND source_type <> 'workflow.child' ORDER BY created_date DESC, id
-- DESC` is an index-only equality-seek + ordered range, no recheck on source_type, no sort. team_id stays a cheap
-- recheck (a workflow belongs to exactly one team, so every matched row passes it). Idempotent (IF NOT EXISTS).

CREATE INDEX IF NOT EXISTS idx_workflow_run_workflow_keyset
    ON workflow_run (workflow_id, created_date DESC, id DESC)
    WHERE workflow_id IS NOT NULL AND source_type <> 'workflow.child';

-- Drop the superseded per-workflow index. idx_workflow_run_by_workflow_started (0009) was (workflow_id, started_at
-- DESC) for a per-workflow runs list ordered by started_at. Every workflow-scoped read now orders by created_date
-- (started_at is re-stamped on resume) and rides the keyset index above, so the old one is pure write-amplification
-- on the hottest-written table — the same redundancy 0062 removed for the twin idx_workflow_run_team_started. No
-- query orders workflow_run by (workflow_id, started_at) anymore (verified).
DROP INDEX IF EXISTS idx_workflow_run_by_workflow_started;
