-- 0022_project.sql
--
-- Phase 3.0 — Project as a first-class container.
--
-- Previously: Repositories sat at Team level, sidebar nav had "Repositories" + "Workflows".
-- Now: Repositories belong to a Project; sidebar has "Workflows" + "Projects"; Repo browsing
-- happens inside a Project's detail page.
--
-- Project also serves as a Variable namespace — workflows reference its variables as
-- `project.{slug}.{var_name}`. Project does NOT own Workflows (Workflows stay at Team level
-- — they're reusable across projects via variable refs); it ONLY owns Repositories +
-- Variables.
--
-- Migration shape (pure additive + backfill, no destructive ops):
--   1. Create `project` table.
--   2. Auto-create one "Default" Project per existing Team (system seed).
--   3. Add `repository.project_id` NOT NULL → backfill every existing Repository to its
--      team's Default Project.
--   4. The Variable table is UNCHANGED — its `scope` discriminator is already polymorphic
--      (see 0012_variable.sql preamble). VariableScope.Project = 2 is purely an app-layer
--      addition.

-- ─── 1. Project table ───────────────────────────────────────────────────────
CREATE TABLE project (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                     UUID NOT NULL REFERENCES team(id),

    -- Variable-path identifier — `project.{slug}.{var}` refs resolve via (team_id, slug).
    -- URL-safe + identifier-safe (alphanumeric + _ + -, no dots).
    slug                        VARCHAR(64)  NOT NULL,
    name                        VARCHAR(128) NOT NULL,
    description                 TEXT NULL,

    -- Soft-delete. Workflows referencing a soft-deleted Project's slug fail validation on
    -- next save with an actionable "project X not found" error.
    deleted_date                TIMESTAMPTZ NULL,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,

    CONSTRAINT chk_project_slug_format
        CHECK (slug ~ '^[A-Za-z0-9_-]{1,64}$')
);

CREATE UNIQUE INDEX uq_project_team_slug_active
    ON project(team_id, slug) WHERE deleted_date IS NULL;

CREATE INDEX idx_project_team_active
    ON project(team_id) WHERE deleted_date IS NULL;

COMMENT ON TABLE project IS
    'Phase 3.0 — first-class container for Repositories + Variables. Workflows stay at Team '
    'level (reusable across Projects via project.{slug}.X variable refs). Default Project '
    'auto-created per team; new bind flow defaults to Default unless caller picks another.';

-- ─── 2. Default Project per existing Team ───────────────────────────────────
-- Bind flow + repository.project_id NOT NULL both rely on every team having at least one
-- Project. Seed one named "Default" per existing team. New teams created post-migration
-- get one via the application layer (see TeamService when we wire it).
INSERT INTO project (id, team_id, slug, name, description, created_date, created_by, last_modified_date, last_modified_by)
SELECT
    gen_random_uuid(),
    t.id,
    'default',
    'Default',
    'Default project for repositories and variables. Auto-created when this team was provisioned; rename or add additional projects as your team grows.',
    NOW(),
    '00000000-0000-0000-0000-000000000001',   -- SystemUsers.SeederId
    NOW(),
    '00000000-0000-0000-0000-000000000001'
FROM team t;

-- ─── 3. repository.project_id (NOT NULL, backfilled to Default) ────────────
-- Step A: add as nullable so we can backfill before tightening.
ALTER TABLE repository ADD COLUMN project_id UUID NULL REFERENCES project(id);

-- Step B: backfill every existing Repository to its team's Default project.
UPDATE repository r
SET project_id = (
    SELECT p.id FROM project p
    WHERE p.team_id = r.team_id AND p.slug = 'default' AND p.deleted_date IS NULL
    LIMIT 1
);

-- Step C: tighten to NOT NULL. Any row without a project_id at this point would mean a
-- team has no Default project, which the seed above guarantees against — so this is safe.
ALTER TABLE repository ALTER COLUMN project_id SET NOT NULL;

-- Index for "list repositories in a project" — hot path on the project-detail page.
CREATE INDEX idx_repository_project_active
    ON repository(project_id) WHERE deleted_date IS NULL;

COMMENT ON COLUMN repository.project_id IS
    'Phase 3.0 — Repositories now belong to a Project. Bind flow takes a ProjectId (defaults '
    'to the team''s Default project). Variable refs work the other way: workflows reference '
    'project.{slug}.X regardless of which repos are linked to that project.';
