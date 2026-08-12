-- 0113_team_invitation.sql
-- Invitations are the only way an account comes into existence: there is no public sign-up, and
-- until now there was no second path either — the seeded admin was the only account the system
-- could ever have.
--
-- The token is never stored. Only its SHA-256 is, exactly as the webhook secrets and the run
-- callback tokens are handled: a database dump must not hand its reader a working invitation.

CREATE TABLE team_invitation (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id               UUID NOT NULL REFERENCES team(id),

    -- The invitation is BOUND to this address. Acceptance must match it, so a forwarded link
    -- cannot be redeemed by whoever received the forward.
    email                 TEXT NOT NULL,
    role                  TEXT NOT NULL CHECK (role IN ('Owner','Admin','Member','Viewer')),

    -- SHA-256 hex of the token. The plaintext exists once, in the response to the member who
    -- created the invitation, and is never recoverable afterwards — regenerate replaces it.
    token_hash            TEXT NOT NULL,

    status                TEXT NOT NULL CHECK (status IN ('Pending','Accepted','Revoked')),
    expires_at            TIMESTAMPTZ NOT NULL,

    invited_by_user_id    UUID NOT NULL REFERENCES app_user(id),
    accepted_by_user_id   UUID REFERENCES app_user(id),
    accepted_at           TIMESTAMPTZ,

    created_date          TIMESTAMPTZ NOT NULL,
    created_by            UUID NOT NULL,
    last_modified_date    TIMESTAMPTZ NOT NULL,
    last_modified_by      UUID NOT NULL
);

-- One live invitation per address per team. Partial, so a revoked or accepted invitation does not
-- block re-inviting someone later — the same shape migration 0008 uses for personal teams.
-- lower(email) because an address is case-insensitive and two casings are the same person.
CREATE UNIQUE INDEX idx_team_invitation_pending_email ON team_invitation (team_id, lower(email)) WHERE status = 'Pending';

-- Lookup is BY HASH and must be unique: the token is the whole credential, so a collision would
-- be an authorization bug, not a data-quality one.
CREATE UNIQUE INDEX idx_team_invitation_token_hash ON team_invitation (token_hash);

CREATE INDEX idx_team_invitation_team ON team_invitation (team_id, status);
