-- 0081_agent_definition_pack_source.sql
--
-- The unified SYNC identity for IMPORTED agents, mirroring skill_definition's uq_skill_definition_pack_source
-- (0079): one active agent_definition row per (pack, file). A re-sync upserts on this pair, never duplicating —
-- the same idempotency guarantee skills already have. agent_definition has carried pack_id / source_path since
-- 0042 (then a soft ref, no FK); the URL-pack commit path is the first writer of those columns, so this index
-- has nothing to backfill (every existing row has pack_id NULL and is excluded by the partial predicate).
--
-- Partial so authored agents (pack_id NULL) are excluded and a soft-deleted row never blocks a re-import of the
-- same file. Additive + non-breaking: a new index only. Idempotent (IF NOT EXISTS).

CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_definition_pack_source
    ON agent_definition(pack_id, source_path) WHERE pack_id IS NOT NULL AND deleted_date IS NULL;
