-- 0005_oauth_pending_state.sql
-- Short-lived rows that bind an OAuth flow's CSRF state to its PKCE verifier and intended target.
--
-- Lifecycle:
--   1. /api/credentials/oauth/init creates a row { state, code_verifier, provider_instance_id,
--      team_id, initiator_user_id, intended_display_name, ... } with ~10 min TTL.
--   2. Provider redirects user back to /api/credentials/oauth/callback?code&state.
--   3. Callback looks up the row by `state` (PK + unforgeable 32-byte random), verifies it
--      hasn't expired, deletes it (one-time use), then exchanges the code using the stored
--      code_verifier and persists a Credential row.
--
-- Security properties:
--   - state is unforgeable → attacker can't trigger a callback with a known state
--   - one-time use (DELETE on consume) → replay rejected
--   - TTL → leaked states can't be replayed long after the user moved on
--   - code_verifier never leaves the server → attacker who intercepts the redirect can't
--     complete the token exchange (PKCE binding)
--
-- A background cleanup job sweeps expired rows; index supports it.

CREATE TABLE oauth_pending_state (
    state                   TEXT PRIMARY KEY,
    provider_instance_id    UUID NOT NULL REFERENCES provider_instance(id),
    team_id                 UUID NOT NULL REFERENCES team(id),
    initiator_user_id       UUID NOT NULL REFERENCES app_user(id),
    code_verifier           TEXT NOT NULL,
    intended_display_name   TEXT NOT NULL,
    intended_owner_user_id  UUID REFERENCES app_user(id),
    return_url              TEXT,
    requested_scopes        TEXT[],
    expires_date            TIMESTAMPTZ NOT NULL,

    created_date            TIMESTAMPTZ NOT NULL,
    created_by              UUID NOT NULL,
    last_modified_date      TIMESTAMPTZ NOT NULL,
    last_modified_by        UUID NOT NULL
);

-- Cleanup sweep target: "delete oauth_pending_state where expires_date < now()".
CREATE INDEX idx_oauth_pending_state_expires ON oauth_pending_state (expires_date);
