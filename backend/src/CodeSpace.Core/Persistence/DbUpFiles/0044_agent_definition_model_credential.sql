-- 0044_agent_definition_model_credential.sql
--
-- A persona's DEFAULT model credential: the ModelCredential (0043) an agent.run run authenticates with when
-- the node doesn't pin one. A REFERENCE (id) only — never the key. Resolved + decrypted just-in-time in the
-- executor; NULL falls back to a team default / operator-global key. Deliberately a SOFT reference (no FK):
-- a persona may be imported/copied across teams, and the run-time resolver is what enforces that the
-- referenced credential belongs to the run's team (a foreign/revoked ref fails the run cleanly) — an FK here
-- would instead hard-block the copy.
--
-- Additive + non-breaking: one nullable column, default NULL (existing personas keep falling back). Idempotent.

ALTER TABLE agent_definition ADD COLUMN IF NOT EXISTS model_credential_id UUID NULL;

COMMENT ON COLUMN agent_definition.model_credential_id IS
    'Default ModelCredential (0043) reference this persona authenticates with; resolved + decrypted just-in-time at run. '
    'NULL = fall back to a team default / operator-global key. Soft ref (no FK) — team ownership is enforced at resolve time.';
