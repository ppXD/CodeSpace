-- 0159_tool_call_ledger_run_created_index.sql
--
-- The governed audit is paged by the truthful legacy-compatible order (CreatedDate, Id), not AdmissionOrdinal:
-- rows predating source admission have NULL ordinals and remain valid audit history. This run-first btree serves
-- Tail and Older keyset reads without OFFSET, COUNT, a whole-ledger scan, or an explicit sort.

CREATE INDEX IF NOT EXISTS idx_tool_call_ledger_run_created_id
    ON tool_call_ledger (agent_run_id, created_date, id);

COMMENT ON INDEX idx_tool_call_ledger_run_created_id IS
    'Bounded governed ToolCall metadata audit: AgentRun identity plus CreatedDate/Id keyset; includes legacy NULL admission ordinals.';
