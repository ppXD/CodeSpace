-- 0019_workflow_run_enqueued.sql
--
-- Phase 2.15 — PostBoy-style dispatch state machine.
--
-- Pre-2.15 lifecycle was:
--     Pending → Running → Success | Failure | Cancelled
-- and the run sat in Pending while an outbox row carried "RunWorkflow" to the engine
-- (an indirection that bought nothing — workflow_run is its own status row and can BE
-- the queue).
--
-- 2.15 collapses the indirection: dispatcher atomically flips
--     Pending → Enqueued (CAS, single-writer)
-- BEFORE handing the run id to the background job client (Hangfire). The engine entry
-- then atomically flips
--     Enqueued → Running (CAS, single-worker)
-- The dual CAS is the no-double-execution guarantee: two dispatchers racing the same
-- run can't both flip Pending→Enqueued (one gets rows-affected=1, the other 0), and two
-- workers picking up the same Hangfire job can't both flip Enqueued→Running.
--
-- Without this migration the application code emits 'Enqueued' but the CHECK constraint
-- from 0009 rejects it, surfacing as Npgsql 23514 at WorkflowsTestSeed.SeedManualRunAsync
-- (and every production dispatch path). This migration:
--   1. Replaces the status CHECK constraint to admit 'Enqueued'.
--   2. Extends idx_workflow_run_active to also cover 'Enqueued' so the reconciler's scan
--      ("find stuck Pending OR stuck Enqueued runs to redispatch") is indexed.

-- ─── 1. Loosen the status CHECK constraint ──────────────────────────────────
-- Drop the auto-named constraint from 0009 and re-add with Enqueued admitted.
ALTER TABLE workflow_run DROP CONSTRAINT workflow_run_status_check;
ALTER TABLE workflow_run ADD CONSTRAINT workflow_run_status_check
    CHECK (status IN ('Pending','Enqueued','Running','Success','Failure','Cancelled'));

COMMENT ON COLUMN workflow_run.status IS
    'Phase 2.15 — extended state machine: Pending → Enqueued → Running → Success/Failure, with '
    'Cancelled as a terminal operator-initiated branch. Pending→Enqueued is the dispatcher''s '
    'atomic CAS BEFORE handing the run to the background job client; Enqueued→Running is the '
    'engine entry''s atomic CAS that claims execution ownership. Two writers cannot both transition '
    'the same row.';

-- ─── 2. Extend the active-runs partial index ─────────────────────────────────
-- The 0009 idx_workflow_run_active partial index covered only Pending+Running — the
-- reconciler that runs on a recurring schedule needs to scan Enqueued rows too (a row
-- that stayed Enqueued past its expected dispatch window is one whose Hangfire enqueue
-- silently dropped; the reconciler revives it via Pending re-dispatch).
DROP INDEX idx_workflow_run_active;

CREATE INDEX idx_workflow_run_active
    ON workflow_run(status) WHERE status IN ('Pending','Enqueued','Running');

COMMENT ON INDEX idx_workflow_run_active IS
    'Phase 2.15 — partial index on non-terminal run states. Supports (a) the dispatcher''s '
    '"runs awaiting enqueue" scan, (b) the reconciler''s "stuck Enqueued or stuck Running" scan, '
    '(c) operator queries for the in-flight queue depth. Excludes terminal Success/Failure/Cancelled '
    'because those rows are read by run-history queries via the (workflow_id, started_at) index, '
    'not by status alone.';
