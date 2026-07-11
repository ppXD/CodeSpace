-- 0039_agent_run.sql
--
-- Durable lifecycle record for one coding-agent harness execution (B0.3). Mirrors `workflow_run`
-- (status flips + lifecycle timestamps + xmin optimistic concurrency) but for the harness-in-sandbox
-- world. Status moves Queued -> Running -> Succeeded/Failed/Cancelled/TimedOut; `heartbeat_at` is the
-- worker liveness ping a stuck-Running reconciler reads to recover crashed runs.
--
-- The full AgentTask envelope lives in `task_jsonb` (so the envelope evolves without schema churn);
-- the normalized AgentRunResult lands in `result_jsonb` on completion. The live event log is a
-- separate append-only table (later slice), not this row.
--
-- `workflow_run_id` / `node_id` link back to the agent.run node that spawned this run; both NULL for
-- a standalone agent run. The run-id link is a deliberate SOFT reference (no FK) — agent runs are
-- managed independently of the workflow-run lifecycle (cross-aggregate). `team_id` keeps its FK to
-- team (the stable root), denormalized for team-scoped queries like every other run-ish table.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS agent_run (
    id                  UUID         NOT NULL PRIMARY KEY,
    team_id             UUID         NOT NULL REFERENCES team(id),
    workflow_run_id     UUID         NULL,
    node_id             TEXT         NULL,
    harness             TEXT         NOT NULL,
    status              TEXT         NOT NULL,
    error               TEXT         NULL,
    task_jsonb          JSONB        NOT NULL DEFAULT '{}',
    result_jsonb        JSONB        NULL,
    heartbeat_at        TIMESTAMPTZ  NULL,
    started_at          TIMESTAMPTZ  NULL,
    completed_at        TIMESTAMPTZ  NULL,
    created_date        TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by          UUID         NOT NULL,
    last_modified_date  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by    UUID         NOT NULL
);

-- Team-scoped, newest-first listing (the Activity / agent-runs surface).
CREATE INDEX IF NOT EXISTS idx_agent_run_team_created ON agent_run(team_id, created_date DESC);

-- "the agent run for this node" lookup — only rows that link back to a run.
CREATE INDEX IF NOT EXISTS idx_agent_run_workflow_run ON agent_run(workflow_run_id) WHERE workflow_run_id IS NOT NULL;

-- Stuck-run reconciler sweep: live runs ordered by last heartbeat. Partial so it stays tiny.
CREATE INDEX IF NOT EXISTS idx_agent_run_running_heartbeat ON agent_run(heartbeat_at) WHERE status = 'Running';

COMMENT ON TABLE agent_run IS
    'Durable lifecycle record of a coding-agent harness execution (Queued -> Running -> terminal). '
    'task_jsonb holds the AgentTask envelope; result_jsonb the normalized AgentRunResult on completion; '
    'heartbeat_at drives stuck-run recovery. workflow_run_id/node_id soft-link the spawning agent.run node.';
