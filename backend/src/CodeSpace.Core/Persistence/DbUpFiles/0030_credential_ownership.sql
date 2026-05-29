-- 0030_credential_ownership.sql
--
-- Credential ownership: Personal (one user's OAuth/PAT) vs TeamService (team-owned, no person —
-- e.g. a GitLab group/project access token or a GitHub App installation). The binding flow prefers
-- a TeamService credential so a repo's connection survives the owner leaving, instead of hinging on
-- whoever bound it first.
--
-- Stored as text (matches auth_type / status, both .HasConversion<string>()). DEFAULT 'Personal'
-- backfills every existing credential — additive + non-breaking.
--
-- Idempotent: IF NOT EXISTS guards a re-run.

ALTER TABLE credential ADD COLUMN IF NOT EXISTS ownership TEXT NOT NULL DEFAULT 'Personal';

COMMENT ON COLUMN credential.ownership IS
    'Personal = tied to one user (OwnerUserId set); TeamService = team-owned, no person (group/'
    'project token, app installation). The repo-binding flow prefers TeamService so a connection '
    'is not a single-user point of failure. Backfilled to Personal for pre-0030 rows.';
