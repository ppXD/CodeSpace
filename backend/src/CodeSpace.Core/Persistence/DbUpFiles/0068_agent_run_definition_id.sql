-- 0068_agent_run_definition_id.sql
--
-- Promote agent_definition_id from agent_run.task_jsonb to a first-class column so the runs index can filter
-- "runs that used agent X". This dimension is fundamentally different from the other run filters: a run's agent set is
-- RUNTIME-EVOLVING (the supervisor spawns fresh agent_runs per turn), so it can NOT be denormalised onto workflow_run
-- at launch. The filter is therefore an EXISTS over agent_run, and the matched key — the persona's AgentDefinitionId —
-- lived only inside task_jsonb (no column, no index). Promote it + index it so the EXISTS is an index probe.
--
-- Nullable: an agent run need not carry a persona (a raw harness task has none). Backfilled from task_jsonb (camelCase
-- 'agentDefinitionId', per AgentJson web options); populated at the creation site (AgentRunService) going forward.
-- The partial index supports the filter's direction ("which runs used these agents"): agent_definition_id leading,
-- workflow_run_id to reach the run. Idempotent.

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS agent_definition_id uuid;

UPDATE agent_run
SET agent_definition_id = (task_jsonb ->> 'agentDefinitionId')::uuid
WHERE agent_definition_id IS NULL
  AND task_jsonb ->> 'agentDefinitionId' IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_agent_run_definition
    ON agent_run (agent_definition_id, workflow_run_id)
    WHERE agent_definition_id IS NOT NULL AND workflow_run_id IS NOT NULL;
