-- Library store/snapshot model (skill-store #5), slice 2: re-scope the uniqueness so a Store snapshot and a
-- Working copy can coexist.
--
-- team_slug uniqueness now applies ONLY to Working rows: a snapshot is never @-mentioned or run, so it needs no
-- unique handle, and a from-store copy may share its snapshot's derived slug. pack_source (the re-sync identity)
-- now applies ONLY to Store rows: idempotent re-import targets the snapshot, so a grandfathered Working row
-- holding the same (pack, source_path) no longer blocks a new Store snapshot of that pair.
--
-- Safe on existing data: at this point every row is scope='Working' (slice 1's default), so the re-scoped
-- team_slug index covers the IDENTICAL set it does today (zero behaviour change) and the re-scoped pack_source
-- index covers ZERO rows (no Store rows exist yet) — neither swap can fail. DROP+CREATE has precedent (0062/0063).

DROP INDEX IF EXISTS uq_agent_definition_team_slug;
CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_definition_team_slug
    ON agent_definition(team_id, slug) WHERE deleted_date IS NULL AND scope = 'Working';

DROP INDEX IF EXISTS uq_agent_definition_pack_source;
CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_definition_pack_source
    ON agent_definition(pack_id, source_path) WHERE pack_id IS NOT NULL AND deleted_date IS NULL AND scope = 'Store';

DROP INDEX IF EXISTS uq_skill_definition_team_slug;
CREATE UNIQUE INDEX IF NOT EXISTS uq_skill_definition_team_slug
    ON skill_definition(team_id, slug) WHERE deleted_date IS NULL AND scope = 'Working';

DROP INDEX IF EXISTS uq_skill_definition_pack_source;
CREATE UNIQUE INDEX IF NOT EXISTS uq_skill_definition_pack_source
    ON skill_definition(pack_id, source_path) WHERE pack_id IS NOT NULL AND deleted_date IS NULL AND scope = 'Store';

-- Supporting partial index for the Library + sync existing-row lookups (pack_id + scope='Store').
CREATE INDEX IF NOT EXISTS idx_agent_definition_pack_store
    ON agent_definition(pack_id) WHERE pack_id IS NOT NULL AND deleted_date IS NULL AND scope = 'Store';
CREATE INDEX IF NOT EXISTS idx_skill_definition_pack_store
    ON skill_definition(pack_id) WHERE pack_id IS NOT NULL AND deleted_date IS NULL AND scope = 'Store';
