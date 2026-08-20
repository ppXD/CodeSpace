-- 0149_workflow_artifact_storage_url_index.sql
--
-- One index, for one question the retention reaper could not ask cheaply: "does any OTHER workflow_artifact row point
-- at this same storage_url". It has to be asked because the local blob backend addresses a payload by SHA alone —
-- LocalFileArtifactBlobBackend writes <root>/<sha[0:2]>/<sha[2:4]>/<sha>, with no team anywhere in the path — while
-- workflow_artifact dedups per (team_id, sha256) and 0016 says so deliberately ("cross-team identical bytes get
-- distinct rows"). So two teams that produce byte-identical content own two rows and ONE file, and a reaper that
-- unlinked that file for one of them would destroy the other team's artifact.
--
-- The probe is on storage_url and not on sha256 because storage_url IS the pointer at the physical file, and sha256 is
-- only a proxy for it that is wrong in both directions worth caring about: the same content is stored inline below the
-- operator's configured threshold (ArtifactStoreConfig.InlineThresholdEnvVar) and routed when the team has a route, so
-- an identical sha can belong to a row that names no file at all; and moving the configured artifact root gives new
-- writes of an identical sha a different url pointing at a genuinely different file.
--
-- Partial, because a reference to a file is the exception: inline rows and routed rows both carry NULL here, and on a
-- deployment that never exceeds the inline threshold this index holds nothing. NOT unique, and it must not be — the
-- whole point is that two rows legally share one url.
--
-- Rollback: DROP INDEX ix_workflow_artifact_storage_url;

-- LOCKS, stated honestly: DbUp runs the entire upgrade inside ONE transaction (DbUpRunner.BuildEngine calls
-- .WithTransaction()) and Postgres releases no lock before that transaction commits, so this CREATE INDEX holds a
-- SHARE lock on workflow_artifact — blocking INSERTs into it — for the length of the whole upgrade run, and the build
-- itself scans every row. The partial predicate keeps the resulting index small but does NOT shorten that scan.
-- CREATE INDEX CONCURRENTLY cannot run inside a transaction, so shortening the window is a change to DbUpRunner, not
-- to this file. This is not new exposure for the table: 0136 already takes ACCESS EXCLUSIVE on it, which is strictly
-- stronger.
CREATE INDEX ix_workflow_artifact_storage_url
    ON workflow_artifact (storage_url)
    WHERE storage_url IS NOT NULL;

COMMENT ON INDEX ix_workflow_artifact_storage_url IS
    'Serves the retention reaper''s cross-team blob-sharing probe. The local blob path is content-addressed and NOT '
    'tenant-scoped, so a purge must first prove no other row of any team points at the same file.';
