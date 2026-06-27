-- 0084_workflow_run_rerun_from_node.sql
--
-- The node a rerun fork re-ran FROM — the fromNode for a rerun-from-node, the map node for a map-branch rerun. NULL
-- for a first run or a whole-run replay. The run detail reads it across a lineage's attempts to show, per node, which
-- attempts re-ran it (a node's own rerun history) without diffing snapshots.
--
-- Idempotent: ADD COLUMN IF NOT EXISTS. No backfill — pre-migration forks carry NULL (no per-node history, degrades
-- to "no badge"), which is correct: we only learned the target from this point on.

ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS rerun_from_node_id text;
