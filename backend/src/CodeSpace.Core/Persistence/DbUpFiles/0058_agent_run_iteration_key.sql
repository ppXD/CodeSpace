-- 0058_agent_run_iteration_key.sql
--
-- Adds agent_run.iteration_key: the owning workflow CELL's iteration key, the same value the engine stamps on
-- the spawning node's workflow_run_node / workflow_run_wait row. Today an agent run carries only
-- (workflow_run_id, node_id), so the N agent branches a map / loop fan-out spawns under ONE node all collapse
-- to the same identity — they cannot be told apart, and a future from-cell rerun can't target one branch.
-- This column completes the (workflow_run_id, node_id, iteration_key) cell address, matching the workflow
-- side (workflow_run_node's composite key) so an agent run joins back to its exact cell.
--
-- Value convention (matches workflow_run_node.iteration_key — TEXT NOT NULL DEFAULT ''):
--   * ''                     — top-level node, standalone run, or non-container case (the NoIteration default)
--   * '<nodeId>#<index>'     — a map branch / loop iteration (nested container keys combine with '/')
--   * '<nodeId>#turn{N}'     — a supervisor turn-spawn (the turn cell)
--
-- Additive + non-breaking: one NOT NULL TEXT column defaulting to '' (every existing run reads as top-level).
-- Idempotent (IF NOT EXISTS).

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS iteration_key TEXT NOT NULL DEFAULT '';

COMMENT ON COLUMN agent_run.iteration_key IS
    'The owning workflow cell''s iteration key (matches workflow_run_node.iteration_key). Completes the '
    '(workflow_run_id, node_id, iteration_key) cell address so the N agent branches a map/loop fan-out spawns '
    'under one node are distinguishable and a from-cell rerun can target one. '''' for top-level / standalone.';
