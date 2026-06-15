-- 0054_workflow_run_wait_supervisor_decision_kind.sql
--
-- The agent.supervisor node (PR-E E2) parks each turn on a 'SupervisorDecision' wait that SELF-ADVANCES
-- the run to the next turn (no external work item). Admit the new kind in the wait_kind CHECK (mirrors
-- 0034/0035/0041). 'SupervisorDecision' is 18 chars, so the original VARCHAR(16) must widen first —
-- unlike the prior kinds it does NOT fit. Idempotent (ALTER TYPE is a no-op when already wide; the CHECK
-- is DROP IF EXISTS + re-ADD).

ALTER TABLE workflow_run_wait DROP CONSTRAINT IF EXISTS workflow_run_wait_wait_kind_check;

ALTER TABLE workflow_run_wait ALTER COLUMN wait_kind TYPE VARCHAR(24);

ALTER TABLE workflow_run_wait
    ADD CONSTRAINT workflow_run_wait_wait_kind_check
    CHECK (wait_kind IN ('Timer', 'Approval', 'Callback', 'Subworkflow', 'Action', 'AgentRun', 'SupervisorDecision'));
