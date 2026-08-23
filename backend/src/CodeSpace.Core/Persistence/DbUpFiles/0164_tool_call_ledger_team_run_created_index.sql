-- 0164_tool_call_ledger_team_run_created_index.sql
--
-- The governed audit predicate is tenant + AgentRun scoped. The original run-first index can lose to the older
-- team/created index once a shared database contains many teams, leaving PostgreSQL to incrementally sort every row
-- for one run before applying the page limit. Put the complete equality prefix and keyset order in one btree so the
-- bounded Tail/Older query remains an ordered index scan regardless of whole-table statistics.

DROP INDEX IF EXISTS idx_tool_call_ledger_run_created_id;

CREATE INDEX idx_tool_call_ledger_run_created_id
    ON tool_call_ledger (team_id, agent_run_id, created_date DESC, id DESC);

COMMENT ON INDEX idx_tool_call_ledger_run_created_id IS
    'Bounded tenant/AgentRun governed ToolCall audit in reverse CreatedDate/Id keyset order; includes legacy NULL admission ordinals.';
