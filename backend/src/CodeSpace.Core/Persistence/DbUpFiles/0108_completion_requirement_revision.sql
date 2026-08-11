-- 0108_completion_requirement_revision.sql
--
-- P1 (v4.3): the APPEND-ONLY requirement-revision ledger. completion_requirement stays the CURRENT projection
-- (one upserted row per (run, kind, requirement_ref), envelope overwritten in place — "the ref is the identity");
-- THIS table is that row's history: one row appended when the current envelope is first staked and again every
-- time it CHANGES, so a revised-instruction retry's re-stake no longer destroys the shape the original attempt
-- was staked under. Since #1321 the staked SpecHash is what ReceiptAdmission compares receipts against — a
-- comparand whose history vanished on every amendment. Receipts binding to a specific revision (v4.3: "receipt
-- 綁 revision+attemptRef+generation") builds on this table in a later slice.
--
-- revision = a table-wide IDENTITY: per-key order is "ORDER BY revision" with no per-key counter to race —
-- concurrent stakes of DIFFERENT envelopes both legitimately append, and the unique-index race on the CURRENT
-- row already collapses concurrent stakes of the SAME envelope (the loser detaches its adds, revisions included).
-- Soft links only (no FK to workflow_run) — the ledger outlives the run, matching completion_requirement.
-- Rollback: DROP TABLE completion_requirement_revision. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS completion_requirement_revision (
    id                  UUID        NOT NULL PRIMARY KEY,
    revision            BIGINT      GENERATED ALWAYS AS IDENTITY,
    team_id             UUID        NOT NULL REFERENCES team(id),
    workflow_run_id     UUID        NOT NULL,
    requirement_ref     TEXT        NOT NULL,
    kind                TEXT        NOT NULL,
    envelope_jsonb      JSONB       NOT NULL,
    created_date        TIMESTAMPTZ NOT NULL,
    created_by          UUID        NOT NULL,
    last_modified_date  TIMESTAMPTZ NOT NULL,
    last_modified_by    UUID        NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_completion_requirement_revision_key
    ON completion_requirement_revision (workflow_run_id, kind, requirement_ref, revision);

CREATE INDEX IF NOT EXISTS ix_completion_requirement_revision_team ON completion_requirement_revision (team_id);
