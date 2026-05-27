-- 0026_project_repository_link_table.sql
--
-- Phase 3.1 — convert Repository:Project from 1:N (FK column on repository) to
-- N:M (link table). Rationale:
--   - A repository may legitimately belong to multiple projects (shared library
--     across squads, monorepo carving). The old single-FK shape locked that
--     possibility out at the schema level.
--   - "Project" should remain the configuration-namespace concept (variables,
--     secrets, trigger UX grouping) without dictating physical ownership.
--   - Dispatch / matchers continue to operate on the Repository, not the
--     Project — PRs naturally belong to repos. Project is purely the UX +
--     organizational layer.
--
-- This migration is INTENTIONALLY NON-MIGRATING: we drop repository.project_id
-- without backfilling project_repository. Existing repos lose their project
-- linkage; operators re-attach via the UI (or via a one-shot SQL script if
-- they want to preserve the prior shape). Accepted trade-off in early dev —
-- there are no production deployments yet.
--
-- Idempotency: every statement is guarded so re-running this migration on an
-- already-applied environment is a no-op.

-- Step A: create the link table. Composite PK on (project_id, repository_id)
-- so the same pair cannot duplicate; soft-delete via deleted_date so an unbind
-- → rebind cycle keeps the audit trail.
CREATE TABLE IF NOT EXISTS project_repository (
    project_id          UUID         NOT NULL REFERENCES project(id) ON DELETE CASCADE,
    repository_id       UUID         NOT NULL REFERENCES repository(id) ON DELETE CASCADE,

    -- Denormalised team_id. Same pattern as workflow_run.team_id:
    -- (a) tenancy-filtered queries skip a join through project;
    -- (b) cross-team illegal-link assertions become a single CHECK at write time
    --     (caller verifies project.team_id == repository.team_id before insert).
    team_id             UUID         NOT NULL,

    created_date        TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by          UUID         NOT NULL,
    last_modified_date  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by    UUID         NOT NULL,
    deleted_date        TIMESTAMPTZ  NULL,

    PRIMARY KEY (project_id, repository_id)
);

-- Step B: hot-path indexes.
--   • by repository → "what projects is this repo in?" (repo detail page)
--   • by project    → "what repos are in this project?" (project detail page)
--   • by team       → "all active links for this team" (admin / move flows)
-- Partial WHERE clauses keep the indexes small (most rows are active).
CREATE INDEX IF NOT EXISTS idx_project_repository_repo_active
    ON project_repository(repository_id)
    WHERE deleted_date IS NULL;

CREATE INDEX IF NOT EXISTS idx_project_repository_project_active
    ON project_repository(project_id)
    WHERE deleted_date IS NULL;

CREATE INDEX IF NOT EXISTS idx_project_repository_team_active
    ON project_repository(team_id)
    WHERE deleted_date IS NULL;

-- Step C: legacy repository.project_id stays for now (dual-write transitional state).
-- A follow-up migration drops it once all read paths consume the link table exclusively
-- and tests are adapted. Keeping the column NOT NULL preserves the existing schema
-- contract and lets this PR ship without touching ~10 test files that seed Repository
-- rows with `ProjectId = ...`.

COMMENT ON TABLE project_repository IS
    'Phase 3.1 — N:M link between Project and Repository. A repository may belong '
    'to many projects (shared libraries, monorepo carving) and a project owns many '
    'repositories. team_id denormalised for tenancy filtering — must match both '
    'project.team_id and repository.team_id at write time (enforced in service). '
    'During the 3.1 transition the legacy repository.project_id column is dual-'
    'written; a follow-up migration drops the column once all readers migrate.';
