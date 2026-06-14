-- 0052_agent_run_inflight_index.sql
--
-- PR-D4a (fail-closed admission control) performance fix. AdmissionController.EnsureAgentRunAdmittedAsync runs on
-- EVERY new agent run, BEFORE it is persisted, and counts the in-flight runs twice: once team-scoped
-- (team_id = @t AND status IN ('Queued','Running')) and once deployment-wide (status IN ('Queued','Running')).
-- Without an index those two COUNTs are a FULL TABLE SCAN of agent_run on the hot creation path; agent_run grows
-- unbounded (one row per agent run ever, the vast majority terminal), so the scan cost climbs forever while the
-- matching set stays small (only the handful of runs actually in flight right now).
--
-- A PARTIAL composite index over (team_id) restricted to exactly the in-flight predicate
-- (status IN ('Queued','Running')) stays small (only Queued/Running rows are indexed) and serves BOTH counts: the
-- team-scoped count is an index-range on team_id within the partial set, and the global count is the partial
-- index's total cardinality (no team filter needed — the WHERE already restricts it to in-flight rows). Status is
-- stored as its string name (see AgentRunConfiguration), so the predicate matches on the literal values.
-- Additive + non-breaking: a new index, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE INDEX IF NOT EXISTS idx_agent_run_inflight
    ON agent_run (team_id)
    WHERE status IN ('Queued', 'Running');
