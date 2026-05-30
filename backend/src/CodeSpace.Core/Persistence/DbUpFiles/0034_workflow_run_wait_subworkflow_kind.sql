-- Engine v2 Phase 3 — sub-workflows. A flow.subworkflow node suspends its run on a
-- 'Subworkflow' wait while a child workflow_run (parent_run_id = this run) executes; the
-- child's completion resumes the parent. Widen the wait_kind CHECK to admit the new kind.
-- VARCHAR(16) already fits 'Subworkflow' (11 chars), so only the constraint changes.

ALTER TABLE workflow_run_wait DROP CONSTRAINT IF EXISTS workflow_run_wait_wait_kind_check;

ALTER TABLE workflow_run_wait
    ADD CONSTRAINT workflow_run_wait_wait_kind_check
    CHECK (wait_kind IN ('Timer', 'Approval', 'Callback', 'Subworkflow'));
