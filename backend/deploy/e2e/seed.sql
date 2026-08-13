-- Deploy compose E2E seed: a non-bootstrap user + team + Owner membership, so a minted JWT can launch a task
-- (the default 0006 admin carries password_must_change, which the launch command path gates — this clean user
-- sidesteps that). Later-added columns (team.kind, app_user.is_bot, password_must_change) all have DEFAULTs, so the
-- minimal column set below is sufficient. Idempotent (ON CONFLICT DO NOTHING) so a re-run is a no-op.
--
-- security_stamp is the exception to "a DEFAULT is enough": it has one, but the API compares the
-- value in the token against the value in this row, so a random default would reject every token the
-- script mints. Pinned here and passed to mint-jwt.js, which is the same pairing a real client gets
-- by signing in.
INSERT INTO app_user (id, email, name, security_stamp, created_date, created_by, last_modified_date, last_modified_by)
VALUES ('11111111-1111-1111-1111-111111111111', 'deploy-e2e@codespace.local', 'Deploy E2E',
        '33333333-4444-5555-6666-777777777777',
        now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001')
ON CONFLICT (id) DO NOTHING;

-- The team row names nobody. Ownership is the Owner membership row below and nothing else since 0118,
-- which renamed the old `owner_user_id` column to `personal_for_user_id` — filled in by a Personal team
-- and NULL on a Workspace like this one.
--
-- This file is hand-written SQL outside DbUp, so a column rename does not break its build; it breaks
-- this lane at runtime, where run.sh pipes it through `psql -v ON_ERROR_STOP=1` and fails the job. Same
-- shape as the security_stamp note above: whatever the schema does, it has to be restated here by hand.
INSERT INTO team (id, slug, name, created_date, created_by, last_modified_date, last_modified_by)
VALUES ('22222222-2222-2222-2222-222222222222', 'deploy-e2e', 'Deploy E2E',
        now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001')
ON CONFLICT (id) DO NOTHING;

INSERT INTO team_membership (id, team_id, user_id, role, created_date, created_by, last_modified_date, last_modified_by)
VALUES ('33333333-3333-3333-3333-333333333333',
        '22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'Owner',
        now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001')
ON CONFLICT (team_id, user_id) DO NOTHING;
