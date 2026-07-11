-- 0041_workflow_run_wait_agent_run_kind.sql
--
-- The agent.run node suspends on an 'AgentRun' wait whose Token is the agent-run id; the agent run's
-- completion resumes the node. Widen the wait_kind CHECK to admit the new kind (mirrors 0034/0035).
-- VARCHAR(16) already fits 'AgentRun'. Idempotent (DROP IF EXISTS + re-ADD).

ALTER TABLE workflow_run_wait DROP CONSTRAINT IF EXISTS workflow_run_wait_wait_kind_check;

ALTER TABLE workflow_run_wait
    ADD CONSTRAINT workflow_run_wait_wait_kind_check
    CHECK (wait_kind IN ('Timer', 'Approval', 'Callback', 'Subworkflow', 'Action', 'AgentRun'));
