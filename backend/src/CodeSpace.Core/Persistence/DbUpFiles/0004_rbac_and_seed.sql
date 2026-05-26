-- 0004_rbac_and_seed.sql
-- RBAC matrix (5 tables, Smarties-style) + seed system user + Admin role.
--
-- Tables:
--   role / permission       — named entities, is_system flag protects seeded rows
--   role_user               — user ↔ role assignments
--   role_permission         — role ↔ permission assignments (role grants permission)
--   user_permission         — direct user ↔ permission grants (bypass role, fine-grained)
--
-- Permission set is intentionally empty in v1 — Admin role bypasses everything at the
-- behavior layer (HasRole(Admin)). Fine-grained permissions land in a later migration
-- when domain-specific checks are added (e.g. workflows:execute).

CREATE TABLE role (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name                 TEXT NOT NULL,
    display_name         TEXT,
    description          TEXT,
    is_system            BOOLEAN NOT NULL DEFAULT false,
    status               BOOLEAN NOT NULL DEFAULT true,

    created_date         TIMESTAMPTZ NOT NULL,
    created_by           UUID NOT NULL,
    last_modified_date   TIMESTAMPTZ NOT NULL,
    last_modified_by     UUID NOT NULL
);

CREATE UNIQUE INDEX idx_role_name ON role (name);


CREATE TABLE permission (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name                 TEXT NOT NULL,
    display_name         TEXT,
    description          TEXT,
    is_system            BOOLEAN NOT NULL DEFAULT false,

    created_date         TIMESTAMPTZ NOT NULL,
    created_by           UUID NOT NULL,
    last_modified_date   TIMESTAMPTZ NOT NULL,
    last_modified_by     UUID NOT NULL
);

CREATE UNIQUE INDEX idx_permission_name ON permission (name);


CREATE TABLE role_user (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    role_id              UUID NOT NULL REFERENCES role(id),
    user_id              UUID NOT NULL REFERENCES app_user(id),

    created_date         TIMESTAMPTZ NOT NULL,
    created_by           UUID NOT NULL,
    last_modified_date   TIMESTAMPTZ NOT NULL,
    last_modified_by     UUID NOT NULL
);

CREATE UNIQUE INDEX idx_role_user_unique ON role_user (role_id, user_id);
CREATE INDEX idx_role_user_by_user ON role_user (user_id);


CREATE TABLE role_permission (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    role_id              UUID NOT NULL REFERENCES role(id),
    permission_id        UUID NOT NULL REFERENCES permission(id),

    created_date         TIMESTAMPTZ NOT NULL,
    created_by           UUID NOT NULL,
    last_modified_date   TIMESTAMPTZ NOT NULL,
    last_modified_by     UUID NOT NULL
);

CREATE UNIQUE INDEX idx_role_permission_unique ON role_permission (role_id, permission_id);


CREATE TABLE user_permission (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id              UUID NOT NULL REFERENCES app_user(id),
    permission_id        UUID NOT NULL REFERENCES permission(id),

    created_date         TIMESTAMPTZ NOT NULL,
    created_by           UUID NOT NULL,
    last_modified_date   TIMESTAMPTZ NOT NULL,
    last_modified_by     UUID NOT NULL
);

CREATE UNIQUE INDEX idx_user_permission_unique ON user_permission (user_id, permission_id);
CREATE INDEX idx_user_permission_by_user ON user_permission (user_id);


-- ── Seed: system user + Admin role ──────────────────────────────────────────────

-- System user is the audit identity for background work (outbox dispatcher, scheduled jobs,
-- DbUp migrations). Pinned UUID — never change. Self-references CreatedBy because there is
-- no prior user to attribute the row to.
INSERT INTO app_user (
    id, email, name,
    created_date, created_by, last_modified_date, last_modified_by
) VALUES (
    '00000000-0000-0000-0000-000000000001',
    'system@codespace.local',
    'System',
    now(), '00000000-0000-0000-0000-000000000001',
    now(), '00000000-0000-0000-0000-000000000001'
);

-- Admin role: is_system=true protects against UI deletion. Bypasses tenancy at the
-- behavior layer. New global-admin capabilities (manage users, set global settings) check
-- for this role.
INSERT INTO role (
    id, name, display_name, description, is_system, status,
    created_date, created_by, last_modified_date, last_modified_by
) VALUES (
    '00000000-0000-0000-0000-000000000010',
    'Admin',
    'Administrator',
    'Full system access. Bypasses tenancy. Used by the system user and any human granted this role explicitly.',
    true, true,
    now(), '00000000-0000-0000-0000-000000000001',
    now(), '00000000-0000-0000-0000-000000000001'
);

-- Bind system user → Admin role.
INSERT INTO role_user (
    id, role_id, user_id,
    created_date, created_by, last_modified_date, last_modified_by
) VALUES (
    gen_random_uuid(),
    '00000000-0000-0000-0000-000000000010',
    '00000000-0000-0000-0000-000000000001',
    now(), '00000000-0000-0000-0000-000000000001',
    now(), '00000000-0000-0000-0000-000000000001'
);
