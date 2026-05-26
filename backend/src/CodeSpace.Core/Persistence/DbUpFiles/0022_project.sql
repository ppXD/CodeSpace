-- 0022_project.sql
--
-- Phase 3.0 — Project as a Variable namespace.
--
-- Project is a first-class entity but has ZERO foreign-key relationship to workflow / repo /
-- workflow_run. It exists for ONE purpose:
--
--   * Group Variables under a named namespace, addressable from workflow definitions as
--     `project.{slug}.{var_name}` (e.g. `project.Backend.SLACK_TOKEN`).
--
-- Deliberately NOT included in this migration:
--   * No workflow.project_id FK            — workflows are Team-resources, not Project-resources
--   * No repository.project_id FK          — repos are Team-resources too
--   * No workflow_run.project_id column    — runs don't carry a project context
--   * No project_workflow link table       — workflows reference projects via variable paths,
--                                            never via membership
--
-- The Variable table itself NEEDS NO SCHEMA CHANGE. Its `scope` discriminator already accepts
-- arbitrary string values (see 0012_variable.sql preamble); `scope_id` becomes polymorphic
-- across Team / Workflow / Project owners. Adding `Project` scope is purely additive at the
-- application layer — VariableScope enum gains a new value, and the resolver learns a new
-- head.
--
-- Future extensions (Project-level cron schedules, per-Project permissions, billing tags)
-- land on this table without forcing schema changes elsewhere.

CREATE TABLE project (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                     UUID NOT NULL REFERENCES team(id),

    -- Slug is the variable-path identifier — `project.{slug}.{var}` references resolve via
    -- (team_id, slug). Constrained to URL-safe + identifier-safe characters by app-layer
    -- validation (the CHECK below enforces minimum hygiene at the DB boundary too).
    slug                        VARCHAR(64)  NOT NULL,

    -- Human display name. Free-form; what the operator sees in the UI.
    name                        VARCHAR(128) NOT NULL,

    -- Optional one-line description for operator clarity. Not consumed by the engine.
    description                 TEXT NULL,

    -- Soft-delete: removing a Project keeps the row for audit trail; workflows that reference
    -- `project.{deleted_slug}.X` start failing validation on next save, giving operators a
    -- clear "this project is gone, fix your workflow refs" signal.
    deleted_date                TIMESTAMPTZ NULL,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,

    -- Slug must be URL-safe + variable-path-safe: alphanumeric + underscore + hyphen.
    -- No dots (would collide with the resolver's `project.X.Y` dotted-path syntax).
    -- No spaces. Min length 1, max length 64 (matches the column width).
    CONSTRAINT chk_project_slug_format
        CHECK (slug ~ '^[A-Za-z0-9_-]{1,64}$')
);

-- One live Project per (team, slug). Soft-deleted rows excluded so an operator can delete
-- a project and recreate the same slug later without conflict — useful for "rename" via
-- delete-then-create or for reorganising experiments.
CREATE UNIQUE INDEX uq_project_team_slug_active
    ON project(team_id, slug) WHERE deleted_date IS NULL;

-- List-projects-for-team is the hot path (Team Settings → Projects tab). Partial index
-- excludes soft-deleted rows so the listing query touches only live rows.
CREATE INDEX idx_project_team_active
    ON project(team_id) WHERE deleted_date IS NULL;

COMMENT ON TABLE project IS
    'Phase 3.0 — Variable namespace. Project has no FK to workflow / repo / workflow_run; '
    'workflows reference Projects only via variable paths like `project.{slug}.{var}`. '
    'Future extensions (schedules, billing, RBAC) hang off this table additively.';

COMMENT ON COLUMN project.slug IS
    'Variable-path identifier. Workflow definitions reference variables in this Project as '
    '`project.{slug}.{variable_name}`. Slug is URL-safe + identifier-safe (alphanumeric + '
    '_ + -), no dots. Unique per team (across live rows).';
