-- 0043_model_credential.sql
--
-- The team-scoped LLM model credential — the API key (and optional base URL) an agent harness uses to
-- authenticate to a model provider (Anthropic / OpenAI / OpenRouter / a self-hosted gateway / Ollama).
--
-- A DISTINCT table from `credential` on purpose: a model key has no git host, so reusing the git credential
-- would force a fake provider_instance and route through the git-only AuthType serializer. Only the
-- encryption primitive is shared (IPayloadEncryptor / DataProtection) — the key lands in
-- `encrypted_api_key` exactly the way every other credential's secret is protected.
--
-- `provider` is a stable STRING tag aligned with ILLMProviderModule.Provider ("Anthropic", "OpenAI", …) —
-- a string, not an enum, so a new provider plugs in with no migration and the agent path can converge with
-- the in-process llm.complete path on one credential source. `encrypted_api_key` is NULLABLE (a keyless
-- local provider reached over `base_url`). `base_url` is plaintext — it's config, not a secret, so the UI
-- can show/edit it without decrypting. `status` mirrors the credential lifecycle; `deleted_date` is
-- soft-delete (a removed credential is treated as unresolvable at the just-in-time resolve, keeping run
-- history intact).
--
-- A workflow run never freezes the key — it freezes a Guid reference to this row, decrypted just-in-time in
-- the executor and injected into the sandboxed child env, never persisted into agent_run / the event log.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS model_credential (
    id                  UUID         NOT NULL PRIMARY KEY,
    team_id             UUID         NOT NULL REFERENCES team(id),
    provider            TEXT         NOT NULL,
    display_name        TEXT         NOT NULL,
    encrypted_api_key   TEXT         NULL,
    base_url            TEXT         NULL,
    status              TEXT         NOT NULL DEFAULT 'Active',
    created_date        TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by          UUID         NOT NULL,
    last_modified_date  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by    UUID         NOT NULL,
    deleted_date        TIMESTAMPTZ  NULL
);

-- Team-scoped listing (the Model Credentials settings surface) + the just-in-time resolve's
-- team-default-by-provider lookup. Partial so soft-deleted rows stay out.
CREATE INDEX IF NOT EXISTS idx_model_credential_team
    ON model_credential(team_id) WHERE deleted_date IS NULL;

COMMENT ON TABLE model_credential IS
    'A team-scoped LLM model credential: the API key (encrypted via IPayloadEncryptor) + optional base_url '
    'an agent harness authenticates with. provider is a string tag (ILLMProviderModule.Provider). A workflow '
    'run freezes only a Guid reference; the key is decrypted just-in-time in the executor, injected into the '
    'sandbox env, and never persisted into agent_run or the event log.';
