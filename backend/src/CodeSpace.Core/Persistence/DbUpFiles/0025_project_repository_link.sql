-- 0025_project_repository_link.sql
--
-- Phase 3.0 recovery — earlier dev environments applied a SLIMMER version of
-- 0022_project.sql (project table only). The current 0022 also adds
-- repository.project_id, but DbUp doesn't re-run scripts that are already in
-- the schemaversions journal, so those environments are missing the column.
--
-- This migration brings them up to par. Idempotent — uses IF NOT EXISTS so a
-- fresh environment (where 0022 already added the column) treats this as a
-- no-op.

-- Step A: add as nullable so we can backfill before tightening.
ALTER TABLE repository ADD COLUMN IF NOT EXISTS project_id UUID NULL REFERENCES project(id);

-- Step B0: seed the "default" project for any team that's missing one. The
-- original 0022 ran for teams that existed at the time, but teams created
-- between migrations (or by an older shape of 0022 that didn't seed) leave
-- their repositories unable to find a Default project to backfill into. Safe
-- to re-run — the ON CONFLICT clause skips teams that already have one.
INSERT INTO project (id, team_id, slug, name, description, created_date, created_by, last_modified_date, last_modified_by)
SELECT
    gen_random_uuid(),
    t.id,
    'default',
    'Default',
    'Default project for repositories and variables. Auto-created when this team was provisioned; rename or add additional projects as your team grows.',
    NOW(),
    '00000000-0000-0000-0000-000000000001',
    NOW(),
    '00000000-0000-0000-0000-000000000001'
FROM team t
WHERE NOT EXISTS (
    SELECT 1 FROM project p
    WHERE p.team_id = t.id AND p.slug = 'default' AND p.deleted_date IS NULL
);

-- Step B: backfill every existing Repository to its team's Default project.
-- Safe to re-run: WHERE project_id IS NULL gates against double-backfill.
UPDATE repository r
SET project_id = (
    SELECT p.id FROM project p
    WHERE p.team_id = r.team_id AND p.slug = 'default' AND p.deleted_date IS NULL
    LIMIT 1
)
WHERE r.project_id IS NULL;

-- Step C: tighten to NOT NULL — idempotent because Postgres treats a SET NOT
-- NULL on an already-NOT-NULL column as a no-op (other than acquiring a brief
-- lock). Wrapped in a DO block so we can probe pg_attribute first and skip
-- altogether on environments where the column is already NOT NULL.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'repository' AND column_name = 'project_id' AND is_nullable = 'YES'
    ) THEN
        ALTER TABLE repository ALTER COLUMN project_id SET NOT NULL;
    END IF;
END $$;

-- Step D: index for "list repositories in a project" (hot path on the
-- project-detail page). CREATE INDEX IF NOT EXISTS is natively idempotent.
CREATE INDEX IF NOT EXISTS idx_repository_project_active
    ON repository(project_id) WHERE deleted_date IS NULL;

COMMENT ON COLUMN repository.project_id IS
    'Phase 3.0 — Repositories belong to a Project. Bind flow takes a ProjectId '
    '(defaults to the team''s Default project). Variable refs work the other way: '
    'workflows reference project.{slug}.X regardless of which repos are linked to '
    'that project.';
