-- 0136_workflow_artifact_storage_route.sql
--
-- Cuts the MAIN artifact plane over to the operator's configured storage. Until now exactly one data class
-- (agent-run-log/v1) resolved a storage_route; every other byte — node payloads, agent diffs, transcripts,
-- model-call bodies, publish manifests, completion evidence — went to a hardcoded local-disk singleton rooted at
-- deployment config, so a team that configured object storage in Settings was quietly told something untrue.
--
-- A routed row holds NEITHER inline bytes NOR a local storage_url: it points at an artifact_object, whose
-- artifact_location records the storage_profile_revision the bytes were placed under. The location ledger — not the
-- current route — is what a read resolves through, so repointing or retiring a route never changes how existing
-- artifacts are found. Rows written before this migration keep inline_bytes/storage_url untouched and stay readable
-- with no route configured at all; a team with no route for workflow-artifact/v1 keeps byte-identical local behaviour.
--
-- Rollback: drop the column, its foreign key and index, and restore the two-arm storage xor.

ALTER TABLE workflow_artifact
    ADD COLUMN cas_artifact_object_id UUID NULL;

-- LOCKS, stated honestly: DbUp runs the ENTIRE upgrade inside one transaction (DbUpRunner.BuildEngine calls
-- .WithTransaction()), and Postgres releases no lock before that transaction commits. The ADD COLUMN above and the
-- DROP CONSTRAINT below each take ACCESS EXCLUSIVE on workflow_artifact, so this file blocks every reader and writer
-- of the table for the length of the whole upgrade run regardless of how the two constraints below are written.
-- What the file CAN do, and does: both constraints are added NOT VALID and are NOT validated here. That does not
-- shorten the lock — it is already held — but it removes two full heap scans of the platform's hottest artifact
-- table from inside the window, so the block is O(1) instead of O(rows). Leaving them unvalidated is sound rather
-- than deferred work: every pre-existing row has cas_artifact_object_id IS NULL and therefore satisfies both by
-- construction, and NOT VALID still enforces on every row written from here on. A genuinely non-blocking cutover
-- would need these statements outside the DbUp transaction, which is a change to the runner, not to this file.
ALTER TABLE workflow_artifact
    ADD CONSTRAINT fk_workflow_artifact_cas_object
    FOREIGN KEY (team_id, cas_artifact_object_id)
    REFERENCES artifact_object (team_id, id) ON DELETE RESTRICT NOT VALID;

-- Exactly one destination per row, now three-way. Every existing row has a NULL cas_artifact_object_id and so
-- satisfies whichever of the first two arms it satisfied before, unchanged.
ALTER TABLE workflow_artifact
    DROP CONSTRAINT workflow_artifact_storage_xor;

ALTER TABLE workflow_artifact
    ADD CONSTRAINT workflow_artifact_storage_xor CHECK (
        (inline_bytes IS NOT NULL AND storage_url IS NULL AND cas_artifact_object_id IS NULL) OR
        (inline_bytes IS NULL AND storage_url IS NOT NULL AND cas_artifact_object_id IS NULL) OR
        (inline_bytes IS NULL AND storage_url IS NULL AND cas_artifact_object_id IS NOT NULL)
    ) NOT VALID;

-- The routed write path counts the intent keys already burned for a piece of content under this exact profile
-- revision, to pick its attempt generation. The pre-existing intent indexes cannot serve it: the recovery indexes
-- are partial on the LIVE states and exclude 'Failed', and the unique idempotency index does not carry state, so
-- the count would scan the team's whole intent range and heap-fetch each row on every offloaded write. This index
-- matches the predicate exactly and stays near-empty on a healthy destination. text_pattern_ops makes the key
-- prefix a range bound regardless of the database's collation.
CREATE INDEX ix_artifact_transfer_intent_failed_key
    ON artifact_transfer_intent (team_id, storage_profile_revision_id, idempotency_key text_pattern_ops)
    WHERE state = 'Failed';

-- Routed reads look up the location by object; the partial index keeps the unrouted majority out of it.
CREATE INDEX ix_workflow_artifact_cas_object
    ON workflow_artifact (team_id, cas_artifact_object_id)
    WHERE cas_artifact_object_id IS NOT NULL;

COMMENT ON COLUMN workflow_artifact.cas_artifact_object_id IS
    'Set when the team routes workflow-artifact/v1 through a configured storage profile. The profile revisions the '
    'bytes were placed under live on the object''s artifact_location rows, and a read resolves through those durable '
    'locations (freshest Available first) — never through the current route.';
