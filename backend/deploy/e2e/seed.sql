-- Deploy compose E2E seed: a non-bootstrap user + team + Owner membership, so a minted JWT can launch a task
-- (the default 0006 admin carries password_must_change, which the launch command path gates — this clean user
-- sidesteps that). Later-added columns (team.kind, app_user.is_bot, password_must_change) all have DEFAULTs, so the
-- minimal column set below is sufficient. Idempotent (ON CONFLICT DO NOTHING) so a re-run is a no-op.
INSERT INTO app_user (id, email, name, created_date, created_by, last_modified_date, last_modified_by)
VALUES ('11111111-1111-1111-1111-111111111111', 'deploy-e2e@codespace.local', 'Deploy E2E',
        now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001')
ON CONFLICT (id) DO NOTHING;

INSERT INTO team (id, slug, name, owner_user_id, created_date, created_by, last_modified_date, last_modified_by)
VALUES ('22222222-2222-2222-2222-222222222222', 'deploy-e2e', 'Deploy E2E',
        '11111111-1111-1111-1111-111111111111',
        now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001')
ON CONFLICT (id) DO NOTHING;

INSERT INTO team_membership (id, team_id, user_id, role, created_date, created_by, last_modified_date, last_modified_by)
VALUES ('33333333-3333-3333-3333-333333333333',
        '22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'Owner',
        now(), '00000000-0000-0000-0000-000000000001', now(), '00000000-0000-0000-0000-000000000001')
ON CONFLICT (team_id, user_id) DO NOTHING;
