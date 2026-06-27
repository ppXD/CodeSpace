-- 0078_pack.sql
--
-- A Pack is an importable LIBRARY of artifacts (agents + skills) a team has added — the "where did this come
-- from" root that makes sync idempotent. A github / git-url pack records its source (url + reference + subpath)
-- so a re-sync resolves to the SAME pack and upserts its artifacts rather than duplicating; the synthetic
-- per-team 'Custom' pack carries no remote source and holds locally-authored artifacts.
--
-- An imported skill_definition (and, later, agent_definition) references its pack by id; the pair
-- (pack, source_path) is the unified sync identity. last_synced_* records the last successful sync.
--
-- `team_id` keeps its FK to team (the stable root). `deleted_date` is soft-delete (removing a pack keeps
-- imported artifacts + history intact). `xmin` is Postgres's system column (mapped by EF), not declared here.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS pack (
    id                  UUID         NOT NULL PRIMARY KEY,
    team_id             UUID         NOT NULL REFERENCES team(id),
    kind                TEXT         NOT NULL,
    name                TEXT         NOT NULL,
    url                 TEXT         NULL,
    reference           TEXT         NULL,
    subpath             TEXT         NULL,
    last_synced_sha     TEXT         NULL,
    last_synced_date    TIMESTAMPTZ  NULL,
    created_date        TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by          UUID         NOT NULL,
    last_modified_date  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by    UUID         NOT NULL,
    deleted_date        TIMESTAMPTZ  NULL
);

-- One active pack per (team, source) so re-adding the same remote resolves to the SAME pack (idempotent sync
-- target). COALESCE(subpath,'') because NULLs compare distinct in a unique index; the synthetic Custom pack
-- (url IS NULL) is excluded so a team may hold exactly one of those without colliding here.
CREATE UNIQUE INDEX IF NOT EXISTS uq_pack_team_source
    ON pack(team_id, url, COALESCE(subpath, '')) WHERE deleted_date IS NULL AND url IS NOT NULL;

-- Team-scoped library listing. Partial so soft-deleted rows stay out.
CREATE INDEX IF NOT EXISTS idx_pack_team
    ON pack(team_id) WHERE deleted_date IS NULL;

COMMENT ON TABLE pack IS
    'An importable library of artifacts (agents + skills) a team added. github/git-url packs record their '
    'source (url + reference + subpath) so re-sync upserts rather than duplicates; the Custom pack holds '
    'locally-authored artifacts. (pack, source_path) on an imported artifact is the unified sync identity.';
