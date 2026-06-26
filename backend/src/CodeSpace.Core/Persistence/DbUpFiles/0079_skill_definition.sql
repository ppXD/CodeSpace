-- 0079_skill_definition.sql
--
-- A reusable Skill — the SKILL.md noun an Agent persona can bind. Mirrors agent_definition's storage gene:
-- only the keys we route/query/@ on are real columns (slug/name/description = the always-cheap Level-1 index),
-- the SKILL.md instruction body is `body` (Level 2, loaded on use), and the original frontmatter is preserved
-- verbatim in `raw_frontmatter_jsonb` so new/unknown keys need NO migration (format-preserving import).
-- Bundled Level-3 resources (references/, scripts/) are NOT stored yet — a later slice adds the manifest.
--
-- Lives in the team's skill library ("skill store"): both authored and pack-imported skills are rows.
-- `pack_id` (FK to pack) + `source_path` are the unified sync identity: a re-sync upserts the row keyed on that
-- pair, never duplicating. Both NULL for an authored skill. `team_id` keeps its FK to team. `deleted_date` is
-- soft-delete (a removed skill keeps agent bindings + history intact). `xmin` is Postgres's system column.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS skill_definition (
    id                      UUID         NOT NULL PRIMARY KEY,
    team_id                 UUID         NOT NULL REFERENCES team(id),
    slug                    TEXT         NOT NULL,
    name                    TEXT         NOT NULL,
    description             TEXT         NULL,
    body                    TEXT         NOT NULL DEFAULT '',
    category                TEXT         NULL,
    raw_frontmatter_jsonb   JSONB        NOT NULL DEFAULT '{}',
    origin                  TEXT         NOT NULL,
    pack_id                 UUID         NULL REFERENCES pack(id),
    source_path             TEXT         NULL,
    created_date            TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by              UUID         NOT NULL,
    last_modified_date      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by        UUID         NOT NULL,
    deleted_date            TIMESTAMPTZ  NULL
);

-- The handle is unique per team among non-deleted skills (a soft-deleted slug can be reused).
CREATE UNIQUE INDEX IF NOT EXISTS uq_skill_definition_team_slug
    ON skill_definition(team_id, slug) WHERE deleted_date IS NULL;

-- The unified SYNC identity: one active row per (pack, file). A re-sync upserts on this, never duplicating.
-- Partial so authored skills (pack_id NULL) are excluded and soft-deleted rows don't block re-import.
CREATE UNIQUE INDEX IF NOT EXISTS uq_skill_definition_pack_source
    ON skill_definition(pack_id, source_path) WHERE pack_id IS NOT NULL AND deleted_date IS NULL;

-- Team-scoped library listing (the skill store surface). Partial so soft-deleted rows stay out.
CREATE INDEX IF NOT EXISTS idx_skill_definition_team
    ON skill_definition(team_id) WHERE deleted_date IS NULL;

-- Re-sync lookup: all skills imported from a given pack. Partial so it stays tiny (authored skills excluded).
CREATE INDEX IF NOT EXISTS idx_skill_definition_pack
    ON skill_definition(pack_id) WHERE pack_id IS NOT NULL;

COMMENT ON TABLE skill_definition IS
    'A reusable Skill (SKILL.md noun) an Agent persona binds: slug/name/description = the Level-1 index, body = '
    'the Level-2 instruction block, raw_frontmatter_jsonb preserves the imported artifact verbatim for lossless '
    'forward-compat. pack_id/source_path are the unified sync identity for idempotent re-sync; harness-agnostic.';
