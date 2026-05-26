-- 0013_release_snapshot.sql
-- Phase 2.8 — release hardening + per-run variable snapshot.
--
-- Three concerns rolled into one migration because they're inseparable design-wise:
--
--   1. workflow_version becomes a true Squid-style release: immutable + content-hashed.
--      Each saved row gets a canonical SHA-256 of its definition_jsonb (computed in
--      application code; persisted here) AND a committed_at anchor that, once set, makes
--      the row tamper-proof at the DB layer via a trigger.
--
--   2. workflow_run captures the release hash at run start, so replay can verify the
--      workflow_version it references hasn't been tampered with. Also adds parent_run_id
--      for re-run lineage.
--
--   3. workflow_run_variable — normalised snapshot of every plain variable value at run
--      start, plus secret-reference rows (name-only, no value). Each row is small;
--      partitioning by run_id keeps the read path O(N) per run with btree lookup. Drives
--      the replay path: plain values frozen from snapshot, secrets re-resolved from the
--      current `variable` table (rotation is a feature).
--
-- All three pieces appear in the same migration because shipping any one without the
-- others creates a half-state where snapshots can't be verified or replayed. Greenfield
-- ops, no production data to preserve, so the changes are additive + immediately effective.

-- ─── 1. workflow_version release hardening ──────────────────────────────────────

ALTER TABLE workflow_version
    ADD COLUMN definition_hash VARCHAR(64) NOT NULL DEFAULT '',
    ADD COLUMN committed_at    TIMESTAMPTZ NULL;

COMMENT ON COLUMN workflow_version.definition_hash IS
    'SHA-256 hex of canonical (sorted-keys, no-whitespace, null-omit) definition_jsonb. '
    'Computed in DefinitionHash.Compute at INSERT time. Empty string only for pre-Phase-2.8 '
    'rows; new rows always carry a non-empty hash.';

COMMENT ON COLUMN workflow_version.committed_at IS
    'Immutability anchor. Set by the application at INSERT time alongside definition_hash. '
    'Once non-null, the row is frozen — the immutability trigger rejects any UPDATE or DELETE.';

-- Trigger function: reject any mutation to a committed row. SQL standard doesn''t have
-- a "row is immutable" constraint, so we enforce it imperatively. The trigger fires
-- before the actual mutation, so the action is blocked entirely (no half-state).
CREATE OR REPLACE FUNCTION workflow_version_enforce_immutability()
RETURNS TRIGGER AS $$
BEGIN
    IF OLD.committed_at IS NOT NULL THEN
        RAISE EXCEPTION 'workflow_version (workflow_id=%, version=%) is committed (committed_at=%) and immutable. '
                        'Tampering with a committed release breaks the replay-integrity check.',
                        OLD.workflow_id, OLD.version, OLD.committed_at;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_workflow_version_immutable_update
    BEFORE UPDATE ON workflow_version
    FOR EACH ROW EXECUTE FUNCTION workflow_version_enforce_immutability();

CREATE TRIGGER trg_workflow_version_immutable_delete
    BEFORE DELETE ON workflow_version
    FOR EACH ROW EXECUTE FUNCTION workflow_version_enforce_immutability();

-- ─── 2. workflow_run capture release hash + parent run lineage ──────────────────

ALTER TABLE workflow_run
    ADD COLUMN release_hash_at_run VARCHAR(64) NOT NULL DEFAULT '',
    ADD COLUMN parent_run_id       UUID NULL REFERENCES workflow_run(id);

-- Extend the trigger_kind CHECK from Phase 2.5 to admit the new Replay value. Drop +
-- recreate is the only way to widen an inline CHECK in PostgreSQL.
ALTER TABLE workflow_run DROP CONSTRAINT workflow_run_trigger_kind_check;
ALTER TABLE workflow_run ADD CONSTRAINT workflow_run_trigger_kind_check
    CHECK (trigger_kind IN ('Manual','Event','Schedule','Replay'));

COMMENT ON COLUMN workflow_run.release_hash_at_run IS
    'Copy of workflow_version.definition_hash captured at run start. Replay verifies this '
    'against the current workflow_version hash — mismatch throws ReleaseTamperedException.';

COMMENT ON COLUMN workflow_run.parent_run_id IS
    'For re-runs, points to the original run that was replayed. NULL for first-time runs. '
    'Drives the run-detail UI''s "Replayed from #N" indicator and audit lineage.';

-- Index supports "show me every re-run of this original run" queries.
CREATE INDEX idx_workflow_run_parent ON workflow_run(parent_run_id) WHERE parent_run_id IS NOT NULL;

-- ─── 3. workflow_run_variable — per-run normalised variable snapshot ────────────
--
-- One row per variable resolved at run start. Normalised (not JSONB blob) for:
--   • Per-row size stays small → TOAST never fires → run-list pagination stays cheap
--   • Audit query "which runs used secret X" becomes a standard btree-indexed WHERE
--   • New scopes (Project / Org / future) add as enum values, no schema change
--
-- Secret rows carry value_type='Secret' + value_plain NULL — the name is recorded for
-- audit but the value is NOT snapshotted. At replay time the engine re-resolves secrets
-- from the current `variable` table; this is intentional (rotation safety).

CREATE TABLE workflow_run_variable (
    id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id          UUID         NOT NULL REFERENCES workflow_run(id) ON DELETE CASCADE,

    -- Generic scope discrimination. New scopes added later are enum-only changes; no
    -- DB migration needed.
    scope           VARCHAR(16)  NOT NULL,  -- 'Wf' | 'Team' | 'Input' | <future>
    name            VARCHAR(128) NOT NULL,
    value_type      VARCHAR(32)  NOT NULL,  -- 'String'|'Number'|'Boolean'|'Object'|'Array'|'Secret'

    -- Plain rows: value_plain is the JSON-encoded value (string preserved with quotes,
    -- number/object/array round-trip without lossy stringification).
    -- Secret rows: value_plain IS NULL.
    value_plain     TEXT         NULL,

    captured_at     TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT chk_wrv_value_consistency CHECK (
        (value_type =  'Secret' AND value_plain IS NULL)
        OR
        (value_type <> 'Secret' AND value_plain IS NOT NULL)
    )
);

-- Primary read path: replay loads every snapshot row for a run.
CREATE INDEX idx_wrv_run_id ON workflow_run_variable(run_id);

-- Uniqueness: each (run, scope, name) tuple appears at most once. Prevents accidental
-- duplicate inserts during the engine's bulk write at run start.
CREATE UNIQUE INDEX uq_wrv_run_scope_name
    ON workflow_run_variable(run_id, scope, name);

-- Audit path: "which runs referenced this secret?" — partial index over secret rows only
-- (typically <5% of rows) keeps the index tiny + write cost low. Query becomes:
--   SELECT run_id FROM workflow_run_variable WHERE scope = ? AND name = ? AND value_type = 'Secret'
CREATE INDEX idx_wrv_secret_audit
    ON workflow_run_variable(scope, name)
    WHERE value_type = 'Secret';

COMMENT ON TABLE workflow_run_variable IS
    'Phase 2.8 — per-run snapshot of resolved variable values. Normalised (one row per '
    'variable) for predictable performance + first-class audit queries. Secret rows record '
    'name-only (no value); plain rows carry JSON-encoded value frozen at run start. Replay '
    'reads from this table for plain values; secrets are re-resolved from `variable`.';
