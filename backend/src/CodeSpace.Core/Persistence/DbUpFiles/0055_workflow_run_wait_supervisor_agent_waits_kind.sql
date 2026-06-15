-- 0055_workflow_run_wait_supervisor_agent_waits_kind.sql
--
-- PR-E E3: a spawn/retry supervisor turn parks on the K real 'AgentRun' waits its executor staged; the node's
-- own suspend uses a 'SupervisorAgentWaits' MARKER (no wait row of that kind is persisted today — the marker
-- short-circuits wait staging in the engine). Admit the kind in the wait_kind CHECK anyway, so the constraint
-- stays an honest superset of WorkflowWaitKinds and a future change that DOES persist a marker row is not
-- rejected by a stale CHECK. 'SupervisorAgentWaits' is 20 chars — fits the existing VARCHAR(24). Idempotent
-- (the CHECK is DROP IF EXISTS + re-ADD).

ALTER TABLE workflow_run_wait DROP CONSTRAINT IF EXISTS workflow_run_wait_wait_kind_check;

ALTER TABLE workflow_run_wait
    ADD CONSTRAINT workflow_run_wait_wait_kind_check
    CHECK (wait_kind IN ('Timer', 'Approval', 'Callback', 'Subworkflow', 'Action', 'AgentRun', 'SupervisorDecision', 'SupervisorAgentWaits'));
