-- 0032_workflow_run_suspended_status.sql
-- Workflow Engine v2 — Phase 1 (suspend / resume), foundation.
--
-- A workflow run pauses when a node returns Suspended (waiting on a timer, a human approval, or
-- an external callback). That paused run needs its OWN run-level status, distinct from Running,
-- for two reasons:
--   1. The stuck-run reconciler marks stale Running runs as abandoned after 30 minutes. A run
--      legitimately waiting on a long sleep or a human approval must NOT be swept — giving it a
--      distinct 'Suspended' status keeps it out of the reconciler's Pending/Enqueued/Running
--      scans automatically.
--   2. It must not look dispatchable, or it would be re-enqueued into a suspend -> re-suspend
--      loop. Resume is an explicit Suspended -> Pending transition driven by the wake signal.
--
-- This migration only widens the status CHECK to admit 'Suspended' (same shape as 0019, which
-- added 'Enqueued'). The active-runs partial index is deliberately NOT extended: a Suspended run
-- is parked, not awaiting dispatch, so it should stay out of the dispatcher/reconciler scan set.
-- Purely additive — no existing row or behaviour changes.

ALTER TABLE workflow_run DROP CONSTRAINT workflow_run_status_check;
ALTER TABLE workflow_run ADD CONSTRAINT workflow_run_status_check
    CHECK (status IN ('Pending','Enqueued','Running','Success','Failure','Cancelled','Suspended'));

COMMENT ON COLUMN workflow_run.status IS
    'Engine v2 Phase 1 — state machine: Pending -> Enqueued -> Running -> Success/Failure, with '
    'Cancelled (operator) and Suspended (node paused on a timer / approval / callback) as the '
    'non-Success exits. Suspended is intentionally parked: the reconciler scans only '
    'Pending/Enqueued/Running, so it survives every sweep. A resume signal flips Suspended -> '
    'Pending and re-dispatches; the durable walker rehydrates and continues. Pending->Enqueued '
    'and Enqueued->Running are the dispatcher / engine atomic CAS transitions (single-writer).';
