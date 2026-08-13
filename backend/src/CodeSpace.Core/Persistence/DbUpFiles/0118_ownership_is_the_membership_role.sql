-- 0118_ownership_is_the_membership_role.sql
-- Ownership stops being a column and becomes what it already was everywhere else: the Owner role on
-- a `team_membership` row.
--
-- Recording it twice gave the question two answers, and they drifted apart in every direction:
--
--   * 0116 repaired teams whose column named an owner that had no row. That was one half.
--   * Leaving a team deletes the membership row and does not touch the column, so the departed person
--     kept resolving to Owner (`TeamMembershipResolver` reads `IsOwner = t.owner_user_id == userId`).
--     Being removed and being demoted had the same shape -- the row moved, the column did not, and
--     the demotion was cosmetic.
--   * The column WON over the row, so an account the column named read as Owner even where its
--     membership row said Viewer.
--
-- One answer is the fix. It is also how everyone else models it: GitLab makes Owner `access_level: 50`
-- on a member row and states the invariant over the member set; a GitHub organisation carries no owner
-- field at all; Google Cloud and Azure keep authority in a role-binding policy beside the resource. A
-- denormalised owner column earns its place when it carries namespace identity (`owner/repo`) or an
-- untransferable singleton (an AWS management account). Neither is true here.
--
-- The column survives for ONE reason: `idx_team_personal_per_user_active` enforces "one active
-- Personal team per user" as a partial unique index, and Postgres cannot build a unique index on
-- `team` from a column in `team_membership`. So it is renamed to what it actually still means --
-- `personal_for_user_id`, the account a Personal team IS -- emptied for every other kind of team, and
-- read by nothing but that index. The rename is the point: every C# read site fails to compile rather
-- than quietly keeping the second answer.
--
-- DEPLOYING THIS: the rename has no expand/contract phase, so it is not safe to roll. DbUpRunner's own
-- note says the api and worker pods run the same assembly and both migrate at startup, which means a
-- rolling deploy routinely has them up together -- and from the moment the first new pod migrates, every
-- still-running old pod selecting team.owner_user_id errors on every team-scoped request. Stop the old
-- pods first, or accept a window of 500s for as long as the rollout takes. Two releases would avoid it
-- (add the new column, deploy readers of both, drop the old) and are not worth it here: the point of the
-- rename is that a stale read cannot compile, and an expand phase is exactly a window where it can.


-- 1. Repair, narrowly. A live team with NO Owner row at all takes the account its column names.
--
-- Guarded on NOT EXISTS rather than backfilled unconditionally, because the column still names people
-- who legitimately left or were removed -- that is the bug being fixed here, and an unconditional
-- backfill would re-add every one of them as an Owner. A team that already has an Owner row already
-- has its answer, whoever the column happens to name.
--
-- The account has to still be there. Handing a team to a soft-deleted or deactivated account is not a
-- repair -- it names an owner nobody can sign in as, which is the unrecoverable state this whole
-- change exists to avoid.
--
-- ON CONFLICT keys on the (team_id, user_id) unique index from 0008: if the named account already has
-- a row on this team saying something else, that row is the answer we are switching to and it stands.
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
JOIN app_user u ON u.id = t.owner_user_id
WHERE t.deleted_date IS NULL
  AND u.deleted_date IS NULL
  AND u.deactivated_at IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM team_membership m
      WHERE m.team_id = t.id
        AND m.role = 'Owner'
  )
ON CONFLICT (team_id, user_id) DO NOTHING;


-- ...and says which teams it did that to.
--
-- The repair cannot tell "never had a row" from "had one and was removed": membership is a hard delete,
-- so the column is the only evidence left either way. Handing an ownerless team back to the account it
-- names is the better of the two available answers -- the alternative is a team nobody inside it can
-- administer -- but it can restore someone a person deliberately took out, so it does not get to happen
-- quietly. Named here, an operator can read the list and undo any of it.
DO $$
DECLARE
    repaired TEXT;
BEGIN
    SELECT string_agg(t.slug || ' -> ' || u.email, ', ' ORDER BY t.slug)
    INTO repaired
    FROM team t
    JOIN app_user u ON u.id = t.owner_user_id
    JOIN team_membership m ON m.team_id = t.id AND m.user_id = t.owner_user_id AND m.role = 'Owner'
    WHERE t.deleted_date IS NULL
      AND m.created_date >= NOW() - INTERVAL '1 minute';

    IF repaired IS NOT NULL THEN
        RAISE WARNING 'ownership-is-the-membership-role: these teams had no Owner row and were handed back to the account the old column named: %. If any of them removed that person deliberately, an instance Admin can undo it.', repaired;
    END IF;
END $$;


-- 2. Whatever is still ownerless is LEFT FOR A HUMAN, named in the database's log.
--
-- Both alternatives are worse. Inventing an owner hands a team to somebody who was never given it --
-- the longest-serving Admin is a guess, not a fact. Deleting the team destroys its runs, repositories
-- and workflows to tidy up a column. So this says exactly which teams and stops.
--
-- It is a WARNING and not an EXCEPTION because refusing to deploy over a state this migration cannot
-- repair helps nobody: the recovery path does not need the schema to stay old. An instance Admin
-- bypasses the team-role check entirely (`TeamMembershipResolver` short-circuits on Roles.Admin) and
-- can promote any member to Owner afterwards.
DO $$
DECLARE
    ownerless TEXT;
BEGIN
    SELECT string_agg(t.slug || ' (' || t.id || ')', ', ' ORDER BY t.slug)
    INTO ownerless
    FROM team t
    WHERE t.deleted_date IS NULL
      AND NOT EXISTS (
          SELECT 1 FROM team_membership m
          WHERE m.team_id = t.id
            AND m.role = 'Owner'
      );

    IF ownerless IS NOT NULL THEN
        RAISE WARNING 'ownership-is-the-membership-role: these live teams have no Owner membership row and none was invented for them: %. An instance Admin can promote one of their members to Owner.', ownerless;
    END IF;
END $$;


-- 3. The column becomes what is left of it.
--
-- A rename carries the FK and the partial unique index across on its own -- Postgres tracks both by
-- column identity, not by name. The constraint is renamed anyway and the index is rebuilt anyway, so
-- that someone grepping for the old name finds nothing still alive under it, and so that this file is
-- where the Personal-team invariant is stated on the name it is now stated in.
ALTER TABLE team RENAME COLUMN owner_user_id TO personal_for_user_id;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'team_owner_user_id_fkey' AND conrelid = 'team'::regclass) THEN
        ALTER TABLE team RENAME CONSTRAINT team_owner_user_id_fkey TO team_personal_for_user_id_fkey;
    END IF;
END $$;

-- NOT NULL had to go before the emptying below, and it could not have stayed regardless: a Workspace
-- team is nobody's personal space.
ALTER TABLE team ALTER COLUMN personal_for_user_id DROP NOT NULL;

UPDATE team SET personal_for_user_id = NULL WHERE kind <> 'Personal';

-- One active Personal team per user -- the invariant the column is kept for, restated on its new name.
-- Partial so a soft-deleted Personal team does not block opening a fresh one, matching 0008.
DROP INDEX IF EXISTS idx_team_personal_per_user_active;

CREATE UNIQUE INDEX idx_team_personal_per_user_active
    ON team (personal_for_user_id)
    WHERE kind = 'Personal' AND deleted_date IS NULL;
