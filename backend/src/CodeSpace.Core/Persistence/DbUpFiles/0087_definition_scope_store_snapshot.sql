-- Library store/snapshot model (skill-store #5), slice 1: the additive schema foundation.
--
-- `scope` discriminates a live WORKING row (on the team's bench, @-mentionable, runnable) from a STORE snapshot
-- (a Library item imported from a pack, instantiated into working copies, never run directly). The NOT NULL
-- DEFAULT 'Working' grandfathers every existing row onto the bench — today's imports keep running, bound, and
-- @-mentioned exactly as before.
--
-- source_definition_id + source_version link a from-store WORKING copy to the snapshot it was made from (and the
-- version captured at copy time = the LHS of the sync comparison). content_version is a STORE snapshot's current
-- per-file content hash (the RHS). All scope-FILTERED behaviour — the index predicates, store-vs-bench reads, and
-- imports writing Store rows — lands in later slices; this migration is purely additive and behaviour-invisible
-- (every row is Working and nothing reads the new columns yet).

ALTER TABLE agent_definition
    ADD COLUMN scope text NOT NULL DEFAULT 'Working',
    ADD COLUMN source_definition_id uuid NULL,
    ADD COLUMN source_version text NULL,
    ADD COLUMN content_version text NULL;

ALTER TABLE skill_definition
    ADD COLUMN scope text NOT NULL DEFAULT 'Working',
    ADD COLUMN source_definition_id uuid NULL,
    ADD COLUMN source_version text NULL,
    ADD COLUMN content_version text NULL;
