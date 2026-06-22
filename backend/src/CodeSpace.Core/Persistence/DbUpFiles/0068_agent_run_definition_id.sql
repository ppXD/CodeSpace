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
--
-- No dedicated index: the filter is a correlated EXISTS that, inside the team keyset query, probes agent_run by
-- workflow_run_id per candidate run — already served by idx_agent_run_workflow_run (0039); agent_definition_id is a
-- cheap heap recheck (a run has few agent runs). This is the recheck tier, consistent with status / source / actor.
-- The upgrade for a hot agent-filtered surface is a (workflow_run_id, agent_definition_id) composite (correlated probe,
-- index-only) or (agent_definition_id, workflow_run_id) (if profiling shows the planner inverts to a semi-join) — add
-- it WHEN that surface ships, after profiling picks the order. Idempotent.

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS agent_definition_id uuid;

-- Guard the cast with a uuid-shape regex, not just IS NOT NULL: a present-but-malformed value would abort the whole
-- migration transaction. No producer writes a non-uuid today (the key is serialized from a typed Guid?), but a single
-- legacy / hand-edited row must not fail the deploy.
UPDATE agent_run
SET agent_definition_id = (task_jsonb ->> 'agentDefinitionId')::uuid
WHERE agent_definition_id IS NULL
  AND task_jsonb ->> 'agentDefinitionId' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
