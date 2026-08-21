-- 0157_tool_call_ledger_projection_candidate.sql
--
-- Global bounded discovery for the observation-only ToolCallLedger -> workflow_run_tool_call projector. The source
-- predicate is deliberately partial: legacy rows have no truthful admission order, decision.request is governed
-- decision traffic, and live rows stay owned by the ledger/reapers until a source-authoritative terminal CAS lands.
-- AdmissionOrdinal is copied exactly as CallOrdinal; excluded rows therefore leave honest visible gaps instead of
-- being densely renumbered by a history scan.
--
-- Included columns keep candidate classification on the index leaf. ResultJson, Error, InputHash, idempotency and
-- approval/decision payloads are intentionally absent: the projector never needs those bytes and must not pull them
-- into a hot observation transaction. This index changes no governance, execution, approval, replay or run outcome.

CREATE INDEX IF NOT EXISTS ix_tool_call_ledger_projection_candidate
    ON tool_call_ledger (created_date, id)
    INCLUDE (team_id, agent_run_id, admission_ordinal, tool_kind, status, last_modified_date)
    WHERE admission_ordinal IS NOT NULL
      AND tool_kind <> 'decision.request'
      AND status IN ('Succeeded', 'Failed', 'Denied', 'Expired');

COMMENT ON INDEX ix_tool_call_ledger_projection_candidate IS
    'Global keyset admission for terminal governed side-effect observations. Partial by truthful source eligibility and terminal source authority; no payload/result/error/idempotency/approval bytes are covered.';

COMMENT ON COLUMN workflow_run_tool_call.call_ordinal IS
    'One-based source order within the execution scope. For tool-call-ledger/v1 this is the exact DB-owned AgentRun admission_ordinal: stable and gap-tolerant because decision.request and legacy NULL source rows are excluded, never densely renumbered.';
