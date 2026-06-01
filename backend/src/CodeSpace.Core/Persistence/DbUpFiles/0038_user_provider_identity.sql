-- 0038_user_provider_identity.sql
--
-- Per-user provider identity (Model B): each CodeSpace user links their OWN GitHub/GitLab
-- identity so attributable WRITE operations (approve / request-changes / comment, and future
-- merge / issue) act AS the human, not the repository's shared connection credential.
--
-- Distinct from `credential` (which holds the token + encryption/scope/status infra): this row
-- is the first-class user↔provider mapping that also captures the provider-side profile
-- (username / id / avatar) for real attribution + display. It REFERENCES a credential for the
-- token, so encryption/refresh/status stay single-sourced on `credential` — there is no second
-- status column here; validity tracks the linked credential's status.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS user_provider_identity (
    id                    UUID         NOT NULL PRIMARY KEY,
    user_id               UUID         NOT NULL REFERENCES app_user(id),
    provider_instance_id  UUID         NOT NULL REFERENCES provider_instance(id),
    credential_id         UUID         NOT NULL REFERENCES credential(id),
    provider_user_id      TEXT         NOT NULL,
    provider_username     TEXT         NOT NULL,
    avatar_url            TEXT         NULL,
    created_date          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by            UUID         NOT NULL,
    last_modified_date    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by      UUID         NOT NULL,
    deleted_date          TIMESTAMPTZ  NULL
);

-- One LIVE acting identity per (user, provider instance). Unlink = soft-delete (deleted_date set),
-- which the partial filter excludes so the user can re-link without colliding. This index is also
-- the resolver hot path ("this user's identity on this provider instance", alive only).
CREATE UNIQUE INDEX IF NOT EXISTS uq_user_provider_identity_user_instance
    ON user_provider_identity(user_id, provider_instance_id)
    WHERE deleted_date IS NULL;

COMMENT ON TABLE user_provider_identity IS
    'Per-user provider identity (Model B): maps a CodeSpace user to their own GitHub/GitLab identity '
    'on a provider instance, referencing the credential that holds the token. Used so attributable '
    'write-backs act AS the human, not the repo connection credential. Validity tracks the linked '
    'credential status (no second status column here).';
