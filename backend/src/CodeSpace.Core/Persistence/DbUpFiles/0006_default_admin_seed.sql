-- 0006_default_admin_seed.sql
-- Bootstrap admin: every fresh CodeSpace deployment starts with one human-loggable
-- account so the very first sign-in works without an out-of-band setup step. Same
-- pattern GitLab, Sonarqube, Jenkins, etc. use to break the chicken-and-egg between
-- "to create a user you need to be signed in" and "to sign in you need a user."
--
-- Default credentials:
--   identifier = admin   (or admin@codespace.local — sign-in accepts either)
--   password   = changeme123
--
-- The hash below is PBKDF2-SHA256, 100 000 iterations, against a fixed 16-byte salt
-- (00112233...eeff). The salt + digest are committed on purpose; they're only useful
-- to an attacker if the operator hasn't rotated the password. The password_must_change
-- flag (set TRUE on this row) forces rotation: the API gates every non-rotation
-- command/query behind it until the operator changes the password.
--
-- Self-contained: schema change, user row, role grant, default workspace. All in one
-- file because they're one logical "make the system loggable" step. Every statement
-- is idempotent so a re-run on a partially-applied DB is safe.

-- 1. Schema: the rotation flag — IF NOT EXISTS keeps re-runs safe on DBs that may
-- have applied an earlier split version of this migration.
ALTER TABLE app_user
    ADD COLUMN IF NOT EXISTS password_must_change BOOLEAN NOT NULL DEFAULT FALSE;

-- 2. The bootstrap admin user itself. ON CONFLICT DO NOTHING preserves any operator-
-- driven password / name change on subsequent DbUp runs.
INSERT INTO app_user (
    id, email, name, password_hash, password_must_change,
    created_date, created_by, last_modified_date, last_modified_by
) VALUES (
    '00000000-0000-0000-0000-000000000100',
    'admin@codespace.local',
    'admin',
    'pbkdf2$sha256$100000$ABEiM0RVZneImaq7zN3u/w==$bwP/ed7w84HhCVbgaUB9HeNzng0mvKlsCEMboWQXiYw=',
    TRUE,
    now(), '00000000-0000-0000-0000-000000000001',
    now(), '00000000-0000-0000-0000-000000000001'
) ON CONFLICT DO NOTHING;

-- 3. Grant the Admin role from migration 0004 — bypasses tenancy at the behavior layer.
INSERT INTO role_user (
    id, role_id, user_id,
    created_date, created_by, last_modified_date, last_modified_by
)
SELECT gen_random_uuid(),
       '00000000-0000-0000-0000-000000000010',
       '00000000-0000-0000-0000-000000000100',
       now(), '00000000-0000-0000-0000-000000000001',
       now(), '00000000-0000-0000-0000-000000000001'
WHERE NOT EXISTS (
    SELECT 1 FROM role_user
    WHERE role_id = '00000000-0000-0000-0000-000000000010'
      AND user_id = '00000000-0000-0000-0000-000000000100'
);

-- 4. Default workspace so the SPA shell isn't empty on first run. The operator can
-- rename or delete it once they set up real teams.
INSERT INTO team (
    id, slug, name, description, owner_user_id,
    created_date, created_by, last_modified_date, last_modified_by
) VALUES (
    '00000000-0000-0000-0000-000000000200',
    'default',
    'Default Workspace',
    'Auto-created on first run. Rename or delete once you set up your real teams.',
    '00000000-0000-0000-0000-000000000100',
    now(), '00000000-0000-0000-0000-000000000100',
    now(), '00000000-0000-0000-0000-000000000100'
) ON CONFLICT DO NOTHING;
