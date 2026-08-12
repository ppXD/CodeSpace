-- 0115_teams_create_permission.sql
-- Wakes up the instance-level permission tables for their first real capability.
--
-- The role / permission / role_permission / user_permission tables have existed since 0004 and held
-- nothing: authorization was "Admin bypasses everything" and every other check was team-scoped. This
-- is the first capability that is genuinely NEITHER -- whether you may create a team is not a fact
-- about any team, so a TeamRole cannot express it.
--
-- Deliberately the ONLY instance permission. The team-scoped matrix stays in committed code, where a
-- reviewer can read the whole access policy in one screen; this tier is for the rare grant that
-- varies per ACCOUNT on a given deployment, which is exactly what "who may open a new workspace" is.

INSERT INTO permission (id, name, display_name, description, is_system, created_date, created_by, last_modified_date, last_modified_by)
VALUES (
    '00000000-0000-0000-0000-000000000020',
    'teams.create',
    'Create teams',
    'May open a new workspace. Instance-level: creating a team is not an action inside any team, so no TeamRole can grant it.',
    true,
    now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001'
)
ON CONFLICT (id) DO NOTHING;

-- Admins hold it by role. Anyone else is granted it individually via user_permission, which is the
-- point of putting this tier in data rather than in code.
INSERT INTO role_permission (id, role_id, permission_id, created_date, created_by, last_modified_date, last_modified_by)
VALUES (
    '00000000-0000-0000-0000-000000000021',
    '00000000-0000-0000-0000-000000000010',
    '00000000-0000-0000-0000-000000000020',
    now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001'
)
ON CONFLICT (id) DO NOTHING;
