-- Removes the run-grain artifact lineage table that never got its writer.
--
-- 0127 created workflow_run_artifact_reference with its header stating "This migration does NOT cut over any runtime
-- reader/writer", and 0128 hardened it with immutability and one-way-supersession triggers — guards built for a
-- writer that was to come. Fifty-plus migrations later no production code has ever written or read a row: the only
-- references in the entire repository are the entity, its EF configuration, one DbSet line, and two tests pinning
-- the shape "before any runtime path may consume it".
--
-- The second act never came, and everything it was staged for has since been delivered another way or refuted:
--   - Run-grain "what did this run produce" ships through artifact_manifest and the Room's deliverables block, which
--     production writes and the UI reads.
--   - It cannot serve the reference oracle: it targets artifact_object, a different graph from workflow_artifact,
--     and the oracle's drift detector deliberately excludes that plane.
--   - Wiring a writer late was examined and rejected: the 0128 guards make whatever lineage grain the first writer
--     chooses (attempt, iteration key, role vocabulary) permanent, so a half-designed producer cannot be repaired by
--     a later migration — the exact reason 0127 declined to cut one over in the first place.
--
-- The table is EMPTY in every deployment — no writer has ever existed — so this drop destroys nothing and is fully
-- reversible by re-running 0127's DDL. Dropping the table drops its triggers and indexes with it; the guard
-- function is dedicated to this table (no other trigger names it), so it goes too.
--
-- Rollback: re-create from 0127 (table + indexes) and 0128 (guard function + trigger).

DROP TABLE workflow_run_artifact_reference;
DROP FUNCTION artifact_cas_run_reference_guard();
