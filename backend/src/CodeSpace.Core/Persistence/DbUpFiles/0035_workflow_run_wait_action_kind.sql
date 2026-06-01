-- Engine v2 — interactive chat affordances (the closed-loop review request). A card button
-- (Approve / Request-changes / …) resumes a run parked on an 'Action' wait: the click resolves
-- the wait with a structured { action, by, comment } payload — the generic, authenticated sibling
-- of a 'Callback' resume. Widen the wait_kind CHECK to admit the new kind. VARCHAR(16) already
-- fits 'Action' (6 chars), so only the constraint changes.

ALTER TABLE workflow_run_wait DROP CONSTRAINT IF EXISTS workflow_run_wait_wait_kind_check;

ALTER TABLE workflow_run_wait
    ADD CONSTRAINT workflow_run_wait_wait_kind_check
    CHECK (wait_kind IN ('Timer', 'Approval', 'Callback', 'Subworkflow', 'Action'));
