-- 0064_pending_decision_indexes.sql
--
-- Partial indexes backing the runs filter's pending-decision EXISTS (HasPendingDecision / NeedsAttention). A pending
-- decision lives in one of TWO park backends, so the filter is a two-branch EXISTS; each branch gets a tiny partial
-- index keyed by the join column so the EXISTS is an index probe, not a scan.
--
--   1. Node-grain: the run is parked on a Decision wait (workflow_run_wait, wait_kind='Decision', status='Pending').
--      Keyed by run_id — the EXISTS correlates on workflow_run.id = workflow_run_wait.run_id.
--   2. Agent-grain: a parked decision.request tool-ledger row (tool_call_ledger, tool_kind='decision.request',
--      status='AwaitingApproval', approved_at IS NULL). Keyed by agent_run_id — the EXISTS hops
--      workflow_run.id -> agent_run.workflow_run_id -> agent_run.id -> tool_call_ledger.agent_run_id (agent_run.id is
--      already indexed by idx_agent_run_workflow_run from 0039). The existing idx_tool_call_ledger_due_approvals (0051)
--      is NOT reusable here — it omits the tool_kind='decision.request' filter (it also covers real side-effect
--      approvals) and is keyed by approval_deadline_at, not agent_run_id.
--
-- Idempotent (IF NOT EXISTS).

CREATE INDEX IF NOT EXISTS idx_workflow_run_wait_pending_decision
    ON workflow_run_wait (run_id)
    WHERE wait_kind = 'Decision' AND status = 'Pending';

CREATE INDEX IF NOT EXISTS idx_tool_call_ledger_pending_decision
    ON tool_call_ledger (agent_run_id)
    WHERE tool_kind = 'decision.request' AND status = 'AwaitingApproval' AND approved_at IS NULL;
