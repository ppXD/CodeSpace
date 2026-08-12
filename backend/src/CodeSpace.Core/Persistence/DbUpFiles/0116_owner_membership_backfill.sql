-- 0116_owner_membership_backfill.sql
-- Every team owner gets the membership row every other owner already has.
--
-- Ownership is recorded in two places -- `team.owner_user_id` and an Owner-role `team_membership`
-- row -- and every team created THROUGH THE APP writes both (TeamProvisioningService, and the
-- invitation flow when it provisions a new account's Personal team). Only the seed workspace from
-- 0006 was written by hand, and it set the column without the row. 0008 backfilled the missing rows
-- but scoped itself `WHERE t.kind = 'Personal'`, so the seed workspace -- the team every fresh
-- deployment lands in -- was the one team left half-recorded.
--
-- Reading code compensates for it (an owner without a membership row still resolves to Owner from
-- the team row), so nothing looked wrong. Writing code does not:
--
--   * TransferOwnershipAsync demotes the outgoing owner by editing their membership row. With no row
--     to edit it moved `owner_user_id` and nothing else, and since the roster is
--     `owner UNION memberships`, the outgoing owner vanished from the team entirely.
--   * Leaving, or having your role changed, loads that row and 404s without it.
--
-- Scoped to every team rather than to the seed one: the defect is "an owner with no membership row",
-- and any deployment that drifted into that state for another reason wants the same repair. Teams
-- that already have the row are untouched -- ON CONFLICT keys on the (team_id, user_id) unique index
-- from 0008, so this is safe to run against a database that has been live for months.
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
WHERE t.deleted_date IS NULL
ON CONFLICT (team_id, user_id) DO NOTHING;
