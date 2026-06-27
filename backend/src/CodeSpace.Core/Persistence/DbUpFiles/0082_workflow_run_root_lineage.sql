-- 0082_workflow_run_root_lineage.sql
--
-- The lineage ROOT of a run — the original a replay/rerun chain descends from. NULL means "I am my own root"
-- (a first-time run, or the original being forked from), so the lineage group key is COALESCE(root_run_id, id).
-- A fork inherits its parent's root, so a whole rerun chain shares ONE root; the team Runs index collapses every
-- run sharing a root into a single entry (its LATEST attempt) instead of stacking a new row per rerun.
--
-- Only replay/rerun forks ever carry a non-null value — every other creation site leaves it NULL, byte-identical
-- to the pre-collapse list. parent_run_id is overloaded (a flow.subworkflow CHILD also sets it), but children are
-- excluded from the index and never sit in a rerun chain, so the backfill skips them and they stay NULL.
--
-- Idempotent: ADD COLUMN IF NOT EXISTS + the backfill only writes rows still NULL + CREATE INDEX IF NOT EXISTS.

ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS root_run_id uuid;

-- Backfill existing forks: for every run with a parent (excluding subworkflow children), walk parent_run_id up to
-- the run whose own parent is NULL — that topmost ancestor is the lineage root. Tree (a fork's parent is always an
-- older run), so the recursion terminates with no cycle guard needed.
WITH RECURSIVE chain AS (
    SELECT id AS run_id, parent_run_id AS ancestor
    FROM workflow_run
    WHERE parent_run_id IS NOT NULL AND source_type <> 'workflow.child'
    UNION ALL
    SELECT c.run_id, wr.parent_run_id
    FROM chain c
    JOIN workflow_run wr ON wr.id = c.ancestor
    WHERE wr.parent_run_id IS NOT NULL
)
UPDATE workflow_run t
SET root_run_id = roots.root
FROM (
    SELECT c.run_id, c.ancestor AS root
    FROM chain c
    WHERE NOT EXISTS (SELECT 1 FROM workflow_run p WHERE p.id = c.ancestor AND p.parent_run_id IS NOT NULL)
) roots
WHERE t.id = roots.run_id AND t.root_run_id IS NULL;

-- The lineage access path: "latest attempt per team lineage" (collapse) + "count of attempts sharing a root"
-- (the N-attempts chip). Partial on the same source_type filter the index query uses.
CREATE INDEX IF NOT EXISTS ix_workflow_run_team_lineage
    ON workflow_run (team_id, COALESCE(root_run_id, id), created_date DESC, id DESC)
    WHERE source_type <> 'workflow.child';
