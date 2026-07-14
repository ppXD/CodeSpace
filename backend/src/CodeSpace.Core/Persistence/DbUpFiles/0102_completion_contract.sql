-- 0102_completion_contract.sql
--
-- P2a-2 (v4.2-FINAL, the R track's first slice): the completion protocol's durable requirement/receipt ledger.
-- completion_requirement = WHAT one run owes (one row per (run, kind, requirement_ref) — upserted, never
-- duplicated); completion_receipt = WHAT HAPPENED against one requirement (append-only; exactly-once per
-- (run, kind, requirement_ref, attempt_id, target_key) so a crash-replayed producer lands on the same row).
-- target_key materializes the admission law "cardinality counts DISTINCT targets": TargetRef when the receipt
-- names one, else 'attempt:{attempt_id}' — two receipts from one attempt for one requirement and target are ONE
-- attestation at the constraint, not just at compose time. envelope_jsonb carries the FULL canonical envelope
-- (Messages/Contracts shapes, null-omitted); the indexed columns exist for query, the envelope is the truth.
-- Soft links throughout (no FK to workflow_run/agent_run) — the ledger outlives both, matching publish_manifest.
-- Readers: the P2a-3 composer via ReceiptAdmission (+ selectors). Writers: the supervisor staging chokepoint
-- (requirements) now; the fold/publish/handoff receipt producers arrive with their slices.
-- Rollback: DROP TABLE completion_receipt; DROP TABLE completion_requirement;
-- Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS completion_requirement (
    id                  UUID        NOT NULL PRIMARY KEY,
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

CREATE UNIQUE INDEX IF NOT EXISTS ux_completion_requirement
    ON completion_requirement (workflow_run_id, kind, requirement_ref);

CREATE INDEX IF NOT EXISTS ix_completion_requirement_team ON completion_requirement (team_id);

CREATE TABLE IF NOT EXISTS completion_receipt (
    id                  UUID        NOT NULL PRIMARY KEY,
    team_id             UUID        NOT NULL REFERENCES team(id),
    workflow_run_id     UUID        NOT NULL,
    requirement_ref     TEXT        NOT NULL,
    kind                TEXT        NOT NULL,
    attempt_id          UUID        NOT NULL,
    target_key          TEXT        NOT NULL,
    envelope_jsonb      JSONB       NOT NULL,
    observed_at         TIMESTAMPTZ NOT NULL,
    created_date        TIMESTAMPTZ NOT NULL,
    created_by          UUID        NOT NULL,
    last_modified_date  TIMESTAMPTZ NOT NULL,
    last_modified_by    UUID        NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_completion_receipt
    ON completion_receipt (workflow_run_id, kind, requirement_ref, attempt_id, target_key);

CREATE INDEX IF NOT EXISTS ix_completion_receipt_team ON completion_receipt (team_id);
