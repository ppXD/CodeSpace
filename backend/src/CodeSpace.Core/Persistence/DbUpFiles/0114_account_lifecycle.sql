-- 0114_account_lifecycle.sql
-- Three columns that make a session revocable.
--
-- Until now a JWT was valid for its full 24 hours no matter what happened to the account behind it:
-- changing a password left every stolen token working, and there was no way to shut an account off
-- at all. The token is stateless by design, so revocation has to be a fact the server checks --
-- these are that fact.

-- Deactivation is NOT deletion. It is reversible, the rows the account authored stay attributable,
-- and its audit trail keeps pointing at a real user -- which is why this is its own column rather
-- than a reuse of deleted_date.
ALTER TABLE app_user ADD COLUMN IF NOT EXISTS deactivated_at TIMESTAMPTZ;

-- Bumped whenever every existing session for this account must stop working: a password change, a
-- reset, a deactivation. The value rides in the JWT and is compared on every request, so rotating it
-- invalidates each token minted before the rotation without a revocation list to store or sweep.
ALTER TABLE app_user ADD COLUMN IF NOT EXISTS security_stamp UUID NOT NULL DEFAULT gen_random_uuid();

-- Same treatment as invitation and webhook tokens: only the digest is kept, so a database dump does
-- not contain working password-reset links.
ALTER TABLE app_user ADD COLUMN IF NOT EXISTS password_reset_token_hash TEXT;
ALTER TABLE app_user ADD COLUMN IF NOT EXISTS password_reset_expires_at TIMESTAMPTZ;

CREATE UNIQUE INDEX IF NOT EXISTS idx_app_user_password_reset_token ON app_user (password_reset_token_hash) WHERE password_reset_token_hash IS NOT NULL;
