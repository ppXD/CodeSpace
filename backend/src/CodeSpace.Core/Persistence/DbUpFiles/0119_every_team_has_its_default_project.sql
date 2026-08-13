-- 0119_every_team_has_its_default_project.sql
-- The sentence the product already says, made true for the teams that predate it saying it.
--
-- The Projects page tells whoever opens it that a default project was auto-created for this team, and
-- the project row's own description repeats it: "auto-created when this team was provisioned". Nothing
-- created one. The single caller of EnsureDefaultProjectAsync was the repository-binding flow, so the
-- claim held only for a team that had bound a repository -- never on its first day, and never at all
-- for a team whose owner had not got that far.
--
-- Provisioning writes it now (TeamProvisioningService stages it in the same unit of work as the team
-- and its Owner row). This is that statement for every team already in the database.
--
-- Keyed on the (team_id, slug) partial unique index from 0022, so a team that already has one -- from
-- a repository bind, or from 0025 which did this once before for the teams alive then -- is untouched.
-- Soft-deleted teams are skipped: they are gone, and reviving one is not this file's business.
--
-- created_by is the System user rather than the team's owner. Provisioning attributes it to whoever
-- opened the team because it knows; a backfill does not, and naming the owner here would assert
-- something about a row they never made.
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
WHERE t.deleted_date IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM project p
      WHERE p.team_id = t.id
        AND p.slug = 'default'
        AND p.deleted_date IS NULL
  );
