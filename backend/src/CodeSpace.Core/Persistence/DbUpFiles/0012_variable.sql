-- 0012_variable.sql
-- Phase 2.7 — unified variable storage. Replaces team_secret + the per-workflow JSON
-- environment[] / variables[] arrays. One table, one shape, scope-and-type orthogonal.
--
-- Storage model:
--   scope      — discriminator: 'Team' or 'Workflow'. Future scopes (Project, ChatflowSession,
--                Organization, ...) are additive enum values; no schema change required.
--   scope_id   — polymorphic owner FK: team_id when scope='Team', workflow_id when 'Workflow'.
--                No DB-level FK because PostgreSQL can't express "FK to multiple tables";
--                application layer enforces referential integrity at write time.
--   team_id    — ALWAYS set, even for Workflow scope (denormalised from workflow.team_id).
--                Tenant-filter index sweeps don't need a JOIN; X-Team-Id middleware checks
--                pass through cleanly regardless of scope.
--   value_type — 'String'|'Number'|'Boolean'|'Object'|'Array'|'Secret'. Drives which value
--                column carries the data.
--   value_plain     TEXT NULL  — JSON-encoded value when value_type != Secret. Stored as
--                                JSON so non-string types round-trip without loss
--                                (numbers stay typed, objects/arrays preserve structure).
--   value_encrypted BYTEA NULL — AES-256-GCM envelope when value_type='Secret'. Identical
--                                byte layout to the old team_secret column ([nonce(12) ||
--                                ciphertext || tag(16)]). Encryption key is the same
--                                AesGcmVariableEncryption singleton.
--
-- The two value columns are mutually exclusive — enforced by chk_variable_value_exclusive
-- so application bugs that write to the wrong column are rejected at the DB boundary.
--
-- Soft-delete via deleted_date column (codebase convention; no deleted_by, matching every
-- other table). The partial-unique index on (scope, scope_id, name) WHERE deleted_date IS NULL
-- lets an operator delete + recreate the same name without conflict.
--
-- Greenfield migration — Phase 2.6's team_secret table is dropped, no data preserved.
-- Production hasn't shipped yet, dev databases get scrubbed.

CREATE TABLE variable (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Polymorphic ownership
    scope                       VARCHAR(16) NOT NULL,
    scope_id                    UUID        NOT NULL,
    team_id                     UUID        NOT NULL REFERENCES team(id),

    -- Identity within (scope, scope_id)
    name                        VARCHAR(128) NOT NULL,

    -- Value
    value_type                  VARCHAR(32) NOT NULL,
    value_plain                 TEXT        NULL,
    value_encrypted             BYTEA       NULL,

    description                 TEXT        NULL,

    -- Audit (codebase convention — no deleted_by)
    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID        NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID        NOT NULL,
    deleted_date                TIMESTAMPTZ NULL,

    -- Value column exclusivity drives off value_type. Secret → encrypted column only;
    -- everything else → plain column only. Either ambiguity (both set / neither set / wrong
    -- column for type) is a programmer error and gets a 23514 at the boundary.
    CONSTRAINT chk_variable_value_exclusive CHECK (
        (value_type =  'Secret' AND value_encrypted IS NOT NULL AND value_plain IS NULL)
        OR
        (value_type <> 'Secret' AND value_plain     IS NOT NULL AND value_encrypted IS NULL)
    )
);

-- One live row per (scope, scope_id, name). Soft-deleted rows excluded so a name can be
-- deleted and re-created without a unique-constraint clash.
CREATE UNIQUE INDEX uq_variable_scope_name_active
    ON variable(scope, scope_id, name) WHERE deleted_date IS NULL;

-- Engine-side bulk read at scope-build time: "give me every active variable for this scope".
-- Single index covers both scope=Team and scope=Workflow read paths.
CREATE INDEX idx_variable_scope_active
    ON variable(scope, scope_id) WHERE deleted_date IS NULL;

-- Tenant-filter sweeps (e.g. "delete all variables when the team is hard-deleted").
CREATE INDEX idx_variable_team_active
    ON variable(team_id) WHERE deleted_date IS NULL;

-- Phase 2.6's team_secret table is fully superseded by the unified variable table.
-- All consumers (entity / service / commands / controller / frontend) are removed in
-- Phase 2.7. Drop the table — greenfield, no production users, no data preservation.
DROP TABLE team_secret;
