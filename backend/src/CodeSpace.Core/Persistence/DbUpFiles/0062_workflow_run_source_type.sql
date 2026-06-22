-- 0062_workflow_run_source_type.sql
--
-- Denormalise source_type from workflow_run_request ONTO workflow_run. The team runs index
-- ("every top-level run, newest first, child-workflow runs excluded") filters on source_type
-- on EVERY page load. Two reasons the column must live on the run, not stay a JOIN to the request:
--   1. JOIN-free hot path — the index-only scan reads source_type without touching the request table.
--   2. A PARTIAL index predicate can only reference columns of the table it indexes, so the
--      child-exclusion ('source_type <> workflow.child') must be a workflow_run column to push
--      the exclusion INTO the index rather than filter it after the scan.
--
-- workflow_run_request.source_type is TEXT NOT NULL and every run has exactly one request
-- (run_request_id FK NOT NULL), so the backfill yields a non-null value for every existing row
-- and the column can take NOT NULL without a fallback. Idempotent (IF NOT EXISTS + null-guarded
-- backfill + DROP/ADD on the keyset index).

ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS source_type TEXT;

-- Backfill existing rows from their request. Null-guarded so a re-run is a no-op.
UPDATE workflow_run r
SET source_type = req.source_type
FROM workflow_run_request req
WHERE r.run_request_id = req.id
  AND r.source_type IS NULL;

ALTER TABLE workflow_run ALTER COLUMN source_type SET NOT NULL;

-- The team runs index keyset: equality on team_id, then (created_date DESC, id DESC) for a
-- stable newest-first order with a unique tiebreaker (ready for keyset pagination). PARTIAL on
-- the child-workflow exclusion so nested-execution runs never enter the index the index serves.
-- created_date (NOT started_at) is the order key — started_at is re-stamped on every resume.
DROP INDEX IF EXISTS idx_workflow_run_team_keyset;
CREATE INDEX idx_workflow_run_team_keyset
    ON workflow_run (team_id, created_date DESC, id DESC)
    WHERE source_type <> 'workflow.child';

-- Drop the superseded team index. idx_workflow_run_team_started (0017) was (team_id, started_at DESC) for an
-- earlier team-runs query that ordered by started_at. The index now orders by created_date (started_at is
-- re-stamped on resume) and filters source_type, so idx_workflow_run_team_keyset replaces it outright — no
-- query orders runs by (team_id, started_at) anymore, leaving the old index as pure write-amplification.
DROP INDEX IF EXISTS idx_workflow_run_team_started;

COMMENT ON COLUMN workflow_run.source_type IS
    'Denormalised from workflow_run_request.source_type at row insert (open string: manual / '
    'replay / schedule.cron / workflow.child / provider.github.pull_request / ...). The team runs '
    'index filters + orders on this without joining the request. Set at the two run-creation sites '
    '(RunStarter, RunFromSnapshotStarter); enforced NOT NULL so a missed site fails loud.';
