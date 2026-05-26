-- 0008_team_kind_and_personal_backfill.sql
-- Introduces the Personal vs Workspace distinction on `team`. Personal teams are the
-- single-member solo space every user gets (auto-created on signup; one per user).
-- Workspace teams are the existing multi-member shape. Same row schema, no behavioral
-- code-paths split — tenancy / RBAC / provider / credential / repository all still key
-- off team_id regardless of kind.

ALTER TABLE team ADD COLUMN IF NOT EXISTS kind TEXT NOT NULL DEFAULT 'Workspace';

-- Storage shape lock: only the two known values. A future TeamKind variant gets its own
-- migration that loosens this constraint deliberately.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'team_kind_check') THEN
        ALTER TABLE team ADD CONSTRAINT team_kind_check CHECK (kind IN ('Personal', 'Workspace'));
    END IF;
END $$;

-- Backfill: every user that doesn't already have a Personal team gets one. Skips the
-- system user (00000000-0000-0000-0000-000000000001) — it's a service identity, not a
-- human, and shouldn't own a personal workspace.
INSERT INTO team (id, slug, name, kind, owner_user_id, created_date, created_by, last_modified_date, last_modified_by)
SELECT
    gen_random_uuid(),
    'personal-' || SUBSTRING(u.id::text FROM 1 FOR 8),
    'Personal',
    'Personal',
    u.id,
    NOW(),
    u.id,
    NOW(),
    u.id
FROM app_user u
WHERE u.deleted_date IS NULL
  AND u.id <> '00000000-0000-0000-0000-000000000001'
  AND NOT EXISTS (
      SELECT 1 FROM team t
      WHERE t.owner_user_id = u.id
        AND t.kind = 'Personal'
        AND t.deleted_date IS NULL
  );

-- Owners are implicit members in ICurrentTeam logic (MeQuery includes owned teams in the
-- list regardless of team_membership rows), but the membership row gives an explicit
-- audit point + lets RBAC behaviors that scan membership see the user. Mirror the
-- workspace pattern: owner gets an Owner-role membership.
--
-- team_membership is a hard-delete junction table (no deleted_date column). The unique
-- (team_id, user_id) constraint protects against duplicates; ON CONFLICT DO NOTHING
-- makes the backfill idempotent even if a membership row was somehow seeded earlier.
INSERT INTO team_membership (id, team_id, user_id, role, created_date, created_by, last_modified_date, last_modified_by)
SELECT
    gen_random_uuid(),
    t.id,
    t.owner_user_id,
    'Owner',
    NOW(),
    t.owner_user_id,
    NOW(),
    t.owner_user_id
FROM team t
WHERE t.kind = 'Personal'
  AND t.deleted_date IS NULL
ON CONFLICT (team_id, user_id) DO NOTHING;

-- One active Personal team per user — partial index so resurrected/soft-deleted historical
-- Personal teams don't conflict (consistent with provider_instance / repository pattern).
CREATE UNIQUE INDEX IF NOT EXISTS idx_team_personal_per_user_active
    ON team (owner_user_id)
    WHERE kind = 'Personal' AND deleted_date IS NULL;
