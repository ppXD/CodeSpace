-- 0117_everyone_may_open_a_workspace.sql
-- Opening a workspace stops being a privilege and becomes the default.
--
-- 0115 introduced `teams.create` granted to the Admin role alone, which made opening a workspace
-- something an operator hands out. That is backwards for this product: you become the owner of what
-- you open and invite people into it yourself, so needing someone else's permission to start makes
-- the ordinary path go through an administrator.
--
-- It stays a GRANT rather than becoming "any signed-in caller" so that it remains revocable —
-- taking it back from one account is deleting a row, not shipping a build. New accounts get the same
-- row at creation (TeamInvitationService.GrantDefaultPermissionsAsync); this is that statement for
-- the accounts that already exist.
--
-- Bots excluded: the chat bot is a member of teams but not a person, and nothing should hand it the
-- ability to create them. Soft-deleted accounts excluded for the same reason they are excluded
-- everywhere else — they are gone.
--
-- Idempotent on the (user_id, permission_id) unique index, so a database where some accounts were
-- granted by hand is repaired rather than rejected.
INSERT INTO user_permission (id, user_id, permission_id, created_date, created_by, last_modified_date, last_modified_by)
SELECT
    gen_random_uuid(),
    u.id,
    p.id,
    NOW(),
    '00000000-0000-0000-0000-000000000001',
    NOW(),
    '00000000-0000-0000-0000-000000000001'
FROM app_user u
CROSS JOIN permission p
WHERE p.name = 'teams.create'
  AND u.is_bot = false
  AND u.deleted_date IS NULL
ON CONFLICT (user_id, permission_id) DO NOTHING;
