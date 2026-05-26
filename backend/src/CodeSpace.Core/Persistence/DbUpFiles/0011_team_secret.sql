-- 0011_team_secret.sql
-- Phase 2.6 — per-team encrypted secret storage. Backs the {{env.*}} scope at runtime:
-- the workflow engine reads every active row for the run's team at scope-build time and
-- exposes them under env.<key>. The plaintext NEVER appears in this table; the encryption
-- layer (AesGcmTeamSecretEncryption) writes [nonce(12) || ciphertext || tag(16)] as a
-- single BYTEA. Rotating an organisation's master key is a separate offline job that
-- re-reads + re-writes every row; this schema doesn't pretend to encode the key id.
--
-- Soft-delete is a column (not a row purge) so the existing audit + restore tooling that
-- already understands deleted_date works here too. The partial-unique index keys off
-- (team_id, key) WHERE deleted_date IS NULL — letting an operator delete + re-create the
-- same key without conflict, while two live rows for the same key are impossible.

CREATE TABLE team_secret (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                     UUID NOT NULL REFERENCES team(id),

    -- The {{env.<key>}} reference name. Validated at the API layer (lower-case + digits +
    -- underscore, max 128 chars) — we don't enforce that here because Postgres CHECK
    -- constraints on TEXT are heavy and the API is the only mutation path.
    key                         TEXT NOT NULL,

    -- Always encrypted. See AesGcmTeamSecretEncryption for the byte layout. NULL would
    -- be ambiguous with "empty value" so we forbid it — set is mandatory on create.
    value_encrypted             BYTEA NOT NULL,

    -- Optional one-line hint surfaced in the Team-secrets list. Pure UX; never feeds the
    -- engine's scope.
    description                 TEXT,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,
    deleted_date                TIMESTAMPTZ
);

-- One live row per (team, key). Soft-deleted rows are excluded so a key can be deleted
-- and re-created without a unique-constraint clash.
CREATE UNIQUE INDEX uq_team_secret_team_key_active
    ON team_secret(team_id, key) WHERE deleted_date IS NULL;

-- Engine-side bulk read at scope-build time: "give me every active secret for this team".
CREATE INDEX idx_team_secret_team_active
    ON team_secret(team_id) WHERE deleted_date IS NULL;
