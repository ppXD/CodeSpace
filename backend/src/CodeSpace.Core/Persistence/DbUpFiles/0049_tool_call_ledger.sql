-- 0049_tool_call_ledger.sql
--
-- Durable exactly-once + audit record of one SIDE-EFFECTING MCP tool call within an agent run (item C —
-- governance closed loop). The UNIQUE (agent_run_id, idempotency_key) index IS the exactly-once invariant: a
-- racing duplicate INSERT hits it and the loser reads the winner's row (the dedup path) instead of re-running the
-- side effect. The idempotency key is SERVER-derived (`tool_kind` + SHA-256 of the canonicalized input, see
-- ToolCallKey) — never read from the wire — so a model cannot forge it to replay an old success or defeat dedup;
-- the key already binds the input hash, so a different input is a different key (never silently collapsed).
--
-- Read-only tools are NOT tracked here (no side effect to dedup, no exactly-once need) — only side-effecting tools
-- get a row. `result_jsonb`/`error` hold the ALREADY-REDACTED tool result (the row is itself a leak surface, so the
-- handler redacts BEFORE persisting). `team_id` keeps its FK to team (tenancy on EVERY row, team-scoped queries);
-- `agent_run_id` is a deliberate SOFT reference (no FK, like agent_run_event) — the ledger outlives its run row.
-- `fence_epoch` mirrors agent_run.fence_epoch at claim time and is recorded for AUDIT/forensics — the single-winner
-- guarantee is FIRST-WRITER-WINS on `status` (the status-guarded CAS transitions), NOT an epoch comparison, so a stale
-- revived worker is fenced by LOSING the status CAS, not by `fence_epoch`.
--
-- The approval_* columns + the AwaitingApproval/Expired statuses are reserved for item D (durable mid-turn HITL):
-- nullable + unused by the C vertical (additive, no behavior). Additive + non-breaking: a brand-new table, nothing
-- else touched. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS tool_call_ledger (
    id                    UUID         NOT NULL PRIMARY KEY,
    team_id               UUID         NOT NULL REFERENCES team(id),
    agent_run_id          UUID         NOT NULL,                 -- soft link (no FK), like agent_run_event
    tool_kind             TEXT         NOT NULL,
    idempotency_key       TEXT         NOT NULL,
    input_hash            VARCHAR(64)  NOT NULL,                 -- VARCHAR (not CHAR) to match the EF HasMaxLength(64) intent — no blank-pad surprise
    status                TEXT         NOT NULL,
    result_jsonb          JSONB        NULL,
    error                 TEXT         NULL,
    approval_message_id   UUID         NULL,                     -- item D (durable HITL), unused by C
    approval_token        TEXT         NULL,                     -- item D, unused by C
    approval_deadline_at  TIMESTAMPTZ  NULL,                     -- item D, unused by C
    fence_epoch           BIGINT       NOT NULL DEFAULT 0,
    created_date          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by            UUID         NOT NULL,
    last_modified_date    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by      UUID         NOT NULL
);

-- The exactly-once invariant: one row per (run, idempotency key). A racing duplicate INSERT hits this and the loser
-- reads the winner's row (the dedup path, see ToolCallLedgerService). The key already binds the input hash, so a
-- different input is a different key — never silently collapsed.
CREATE UNIQUE INDEX IF NOT EXISTS ux_tool_call_ledger_run_key ON tool_call_ledger(agent_run_id, idempotency_key);

-- Team-scoped audit read (the per-call log surface), newest first.
CREATE INDEX IF NOT EXISTS idx_tool_call_ledger_team_created ON tool_call_ledger(team_id, created_date DESC);

-- The respond path (item D) locates a pending approval by its token (server-side bearer). Partial so it stays tiny.
CREATE INDEX IF NOT EXISTS idx_tool_call_ledger_approval_token ON tool_call_ledger(approval_token) WHERE approval_token IS NOT NULL;

COMMENT ON TABLE tool_call_ledger IS
    'Item C — durable exactly-once + audit record of a SIDE-EFFECTING MCP tool call within an agent run. '
    'ux_tool_call_ledger_run_key is the exactly-once invariant: a racing duplicate INSERT loses and reads the '
    'winner''s row. idempotency_key is SERVER-derived (tool_kind + SHA-256 of canonical input) — never from the wire. '
    'Read-only tools are not tracked. result_jsonb/error are the ALREADY-REDACTED tool result. approval_* columns + '
    'the AwaitingApproval/Expired statuses are reserved for item D (durable mid-turn HITL), unused by C.';
