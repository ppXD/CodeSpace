-- 0023_workflow_run_lifecycle.sql
--
-- Phase 3.0 hardening — explicit lifecycle timestamps on workflow_run.
--
-- Problem fixed:
--   WorkflowRunDispatcher's CAS UPDATE only sets `status` (no LastModifiedDate update —
--   ExecuteUpdateAsync bypasses EF's audit hook). The stuck-run reconciler was checking
--   `LastModifiedDate < threshold` to find "stale Enqueued" rows. Result: a run that sat
--   in Pending for 11 minutes then got dispatched would IMMEDIATELY look stuck to the
--   reconciler (because LastModifiedDate still reflected the original creation time),
--   causing it to race the engine's Enqueued→Running CAS.
--
-- Solution mirrors the RepositoryWebhook lifecycle (Migration 0020): explicit state-
-- transition timestamps. The reconciler now reads `enqueued_at` instead of inferring
-- staleness from the audit column.
--
--   * enqueued_at — set by WorkflowRunDispatcher when CAS Pending → Enqueued succeeds.
--                   NULL otherwise. Reconciler uses this for the "stuck Enqueued" sweep.
--   * StartedAt   — already exists, set by Engine when CAS Enqueued → Running succeeds.

ALTER TABLE workflow_run ADD COLUMN enqueued_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN workflow_run.enqueued_at IS
    'Phase 3.0 — set by WorkflowRunDispatcher when CAS Pending → Enqueued succeeds. NULL '
    'while in Pending or after reverting to Pending. Reconciler''s "stuck Enqueued" sweep '
    'uses (status=Enqueued AND enqueued_at < now-10min) instead of LastModifiedDate, which '
    'ExecuteUpdateAsync does not update.';
