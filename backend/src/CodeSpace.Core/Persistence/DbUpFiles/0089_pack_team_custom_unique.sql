-- Library authoring: a team gets exactly ONE synthetic "Custom" pack — the home for hand-authored Library
-- agents/skills (store entries the operator instantiates working copies from). The main uq_pack_team_source index
-- excludes url-less packs (WHERE url IS NOT NULL), so the Custom pack (url IS NULL) has no singleton guard there;
-- this partial unique index is it, so EnsureCustomPack's find-or-create can't silently fork two Custom packs.
--
-- Safe on existing data: nothing creates a Custom pack yet (the author-into-Library flow ships with this), so the
-- index covers zero rows on apply.
CREATE UNIQUE INDEX IF NOT EXISTS uq_pack_team_custom
    ON pack(team_id) WHERE kind = 'Custom' AND deleted_date IS NULL;
