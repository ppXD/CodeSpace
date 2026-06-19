-- 0059_model_credential_model.sql
--
-- The maintained model list on a `model_credential` — one row per (credential, model id). The
-- credential-rooted catalog底座: a model is only "runnable" because a credential backs it, so this is a CHILD
-- of model_credential (FK + ON DELETE CASCADE), never a standalone catalog. The team's usable model pool is the
-- union of its active credentials' enabled rows; "a model with no key" is structurally impossible.
--
-- `model_id` is the wire id a harness/decider passes (e.g. 'claude-sonnet-4-5', 'gpt-5.4-codex'); UNIQUE per
-- credential (not global — the same id can be backed by two credentials). `source` is 'Manual' (operator-typed)
-- or 'Reflected' (discovered from the provider's model endpoint); a refresh re-writes Reflected rows but never
-- touches Manual ones. The three capability booleans are the per-model boundary the supervisor/agent scheduling
-- reads (all default FALSE = declares nothing — a safe floor). `enabled` soft-hides a row without deleting it.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. A credential with no rows resolves exactly
-- as before (the just-in-time resolver does not read this table). Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS model_credential_model (
    id                         UUID    NOT NULL PRIMARY KEY,
    model_credential_id        UUID    NOT NULL REFERENCES model_credential(id) ON DELETE CASCADE,
    model_id                   TEXT    NOT NULL,
    display_name               TEXT    NULL,
    source                     TEXT    NOT NULL DEFAULT 'Manual',
    supports_structured_output BOOLEAN NOT NULL DEFAULT FALSE,
    supports_tool_use          BOOLEAN NOT NULL DEFAULT FALSE,
    recommended_for_supervisor BOOLEAN NOT NULL DEFAULT FALSE,
    enabled                    BOOLEAN NOT NULL DEFAULT TRUE
);

-- One model id per credential — makes a reflection refresh idempotent and prevents a manual/reflected collision.
CREATE UNIQUE INDEX IF NOT EXISTS ux_model_credential_model_cred_model
    ON model_credential_model(model_credential_id, model_id);

COMMENT ON TABLE model_credential_model IS
    'A model on a model_credential''s maintained list (manual + reflected), FK-rooted under the credential so a '
    'model cannot exist without a backing key. Carries the per-model capability boundary the supervisor/agent '
    'scheduling reads. The team pool is the union of active credentials'' enabled rows.';
