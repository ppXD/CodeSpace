-- 0053_run_record_iteration_key_prefix_index.sql
--
-- PR-D5 (durable-resume replay) performance fix. The map + loop rehydrate paths re-enter a suspended run by reading
-- every branch / iteration row UNDER a run-scoped iteration-key PREFIX:
--
--   RehydrateMapResultsAsync (WorkflowEngine.cs): WHERE run_id = @r AND iteration_key LIKE '<mapId>#%'
--   RehydrateLoopStateAsync  (WorkflowEngine.cs): WHERE run_id = @r AND iteration_key LIKE '<loopId>#%'
--
-- (both via the workflow_run_node VIEW over this ledger; EF's IterationKey.StartsWith(prefix) translates to that LIKE).
-- The existing per-node index 0015 idx_wrr_run_node (run_id, node_id, iteration_key) LEADS WITH node_id, so the prefix
-- predicate can't bind to it — the planner falls back to idx_wrr_run_sequence (run_id only) and applies the prefix as a
-- post-scan FILTER. That scans EVERY node row of the run (1k / 10k branches for a large or nested map) just to keep the
-- handful under one map's prefix — O(rows-in-run) per rehydrate, climbing with fan-out width.
--
-- This index leads with run_id (the equality) then iteration_key, so the planner serves run_id as an equality bound AND
-- pushes the prefix in as an index range (iteration_key >= '<prefix>' AND iteration_key < '<prefix++>').
--
-- text_pattern_ops is REQUIRED, not optional: the database default collation is en_US.utf8 (a non-C collation — verified
-- against the integration test DB, which inherits template1). Under a non-C collation a PLAIN btree on text CANNOT serve
-- LIKE 'prefix%' — Postgres only pushes the prefix into the index when the index uses the C-locale pattern opclass
-- (text_pattern_ops), whose ~>=~ / ~<~ operators are collation-independent byte comparisons. Verified by EXPLAIN: with a
-- plain btree the prefix stays a Filter; with text_pattern_ops the plan is a Bitmap/Index Scan whose Index Cond carries
-- the prefix range. (The integer-suffix attribution in C# — LoopIterationIndex parsing up to the next '/' — is unchanged;
-- only the PLAN changes, never the rows returned.)
--
-- Partial WHERE node_id IS NOT NULL mirrors 0015 idx_wrr_run_node + the workflow_run_node view's own filter
-- (record_type LIKE 'node.%' AND node_id IS NOT NULL): the rehydrate queries only ever read through that view, so every
-- candidate row has node_id NOT NULL. The partial predicate keeps the index off the run-level (node_id NULL) ledger rows
-- (log lines, scope snapshots) it would never serve, and the planner's Recheck Cond already carries node_id IS NOT NULL
-- so the partial matches.
--
-- Additive + non-breaking + idempotent: a new index on an append-mostly ledger, nothing else touched. Write impact is one
-- more btree maintained on INSERT — negligible against a table that is INSERT-only by contract (the immutability trigger
-- forbids UPDATE/DELETE, so there is no index churn beyond the single append). IF NOT EXISTS.

CREATE INDEX IF NOT EXISTS idx_wrr_run_iteration_prefix
    ON workflow_run_record (run_id, iteration_key text_pattern_ops)
    WHERE node_id IS NOT NULL;
