-- 0042_agent_definition.sql
--
-- The reusable Agent persona — the canonical "Agent" noun (Model A): a named, importable, @-mentionable,
-- directly-runnable capability. An `agent.run` node references one of these (with optional inline
-- overrides); the harness projects it. HARNESS-AGNOSTIC by design (no harness column) so the same persona
-- runs on any compatible harness.
--
-- Generic + scaling: only the keys we route/query/@ on are real columns; the original artifact's full
-- frontmatter is preserved verbatim in `raw_frontmatter_jsonb`, so new/unknown keys need NO migration
-- (format-preserving import). List/structured fields are jsonb (modelled into DTOs by the service layer
-- later). `tools_jsonb` is NULLABLE: NULL = the harness's default toolset (distinct from '[]' = no tools).
--
-- `pack_id` / `source_path` are import provenance (the git pack + ref + file) for re-sync; both NULL for an
-- authored agent. `pack_id` is a deliberate SOFT reference (no FK) — the agent-pack table is a later slice.
-- `team_id` keeps its FK to team (the stable root). `deleted_date` is soft-delete (a removed persona keeps
-- run history intact). `xmin` is Postgres's system column (mapped by EF), not declared here.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS agent_definition (
    id                      UUID         NOT NULL PRIMARY KEY,
    team_id                 UUID         NOT NULL REFERENCES team(id),
    slug                    TEXT         NOT NULL,
    name                    TEXT         NOT NULL,
    description             TEXT         NULL,
    system_prompt           TEXT         NOT NULL DEFAULT '',
    model                   TEXT         NULL,
    default_autonomy        TEXT         NULL,
    tools_jsonb             JSONB        NULL,
    skills_jsonb            JSONB        NOT NULL DEFAULT '[]',
    mcp_servers_jsonb       JSONB        NOT NULL DEFAULT '[]',
    raw_frontmatter_jsonb   JSONB        NOT NULL DEFAULT '{}',
    origin                  TEXT         NOT NULL,
    pack_id                 UUID         NULL,
    source_path             TEXT         NULL,
    created_date            TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by              UUID         NOT NULL,
    last_modified_date      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by        UUID         NOT NULL,
    deleted_date            TIMESTAMPTZ  NULL
);

-- The @-mention handle is unique per team among non-deleted personas (a soft-deleted slug can be reused).
CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_definition_team_slug
    ON agent_definition(team_id, slug) WHERE deleted_date IS NULL;

-- Team-scoped library listing (the Agents library surface). Partial so soft-deleted rows stay out.
CREATE INDEX IF NOT EXISTS idx_agent_definition_team
    ON agent_definition(team_id) WHERE deleted_date IS NULL;

-- Re-sync lookup: all personas imported from a given pack. Partial so it stays tiny (authored agents excluded).
CREATE INDEX IF NOT EXISTS idx_agent_definition_pack
    ON agent_definition(pack_id) WHERE pack_id IS NOT NULL;

COMMENT ON TABLE agent_definition IS
    'A reusable Agent persona (the canonical Agent noun): system prompt + model + tools + skills + MCP + '
    'default autonomy. Harness-agnostic. raw_frontmatter_jsonb preserves the imported artifact verbatim for '
    'lossless forward-compat; tools/skills/mcp are jsonb; pack_id/source_path are import provenance for re-sync.';
