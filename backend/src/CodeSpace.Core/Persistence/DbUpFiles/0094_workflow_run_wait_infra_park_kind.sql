-- 0094_workflow_run_wait_infra_park_kind.sql
--
-- P1.1 of the launch-stability arc: the supervisor's MODEL-PLANE OUTAGE park. When the brain call exhausts its
-- bounded in-call retry on a transient/rate-limit fault, the agent.supervisor node no longer terminalizes the
-- durable run — it parks on a 'SupervisorInfraPark' wait whose DeadlineAt walks an exponential ladder (the
-- deadline IS the wake; nothing else resolves it). Widen the wait_kind CHECK to admit the new kind (mirrors
-- 0041/0060). 'SupervisorInfraPark' is 19 chars — fits the existing VARCHAR(24). Idempotent (DROP IF EXISTS + re-ADD).

ALTER TABLE workflow_run_wait DROP CONSTRAINT IF EXISTS workflow_run_wait_wait_kind_check;

ALTER TABLE workflow_run_wait
    ADD CONSTRAINT workflow_run_wait_wait_kind_check
    CHECK (wait_kind IN ('Timer', 'Approval', 'Callback', 'Subworkflow', 'Action', 'AgentRun', 'SupervisorDecision', 'SupervisorAgentWaits', 'SupervisorInfraPark', 'Decision'));
