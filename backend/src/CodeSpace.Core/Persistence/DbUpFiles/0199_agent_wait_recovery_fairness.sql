ALTER TABLE workflow_run_wait ADD COLUMN last_agent_recovery_attempt_at timestamptz NULL;
CREATE INDEX idx_workflow_run_wait_agent_recovery ON workflow_run_wait (last_agent_recovery_attempt_at ASC NULLS FIRST, created_at, id) WHERE status = 'Pending' AND wait_kind = 'AgentRun';
