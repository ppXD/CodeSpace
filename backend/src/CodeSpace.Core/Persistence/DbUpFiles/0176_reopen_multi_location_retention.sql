-- Reopens artifact retention declarations that were settled terminally for a reason that is no longer true.
--
-- Until the routed purge could name WHICH placement it meant, a claim over an object with more than one placement was
-- refused outright, and the reaper recorded that refusal as ArtifactRetentionDecision.Indeterminate — documented
-- "Terminal keep". Settling stamps terminal_at, and the sweep's claim admits only 'Declared' and 'Quarantined', so
-- nothing in the codebase ever looks at such a row again. The artifact and its bytes are kept forever.
--
-- Reachable with no replication feature at all: one artifact_object is resolved by (team, digest) alone while a
-- location is per (profile revision, object key), so one byte-identical payload written under two revisions — which
-- is exactly what repointing a storage route produces — is one object with two placements.
--
-- Scoped to that ONE error code. Every other Indeterminate reason is a deliberate terminal keep and is left alone.
--
-- The CASE is not cosmetic: ck_workflow_artifact_retention_state (0144:83-89) forbids 'Declared' with a non-null
-- quarantined_at and requires one for 'Quarantined', and settling never clears it. Hardcoding either state would
-- fail the constraint for half the rows.
--
-- Rollback: none needed. A reopened row is simply swept again; if the refusal were somehow still correct the sweep
-- would settle it terminally once more.

UPDATE workflow_artifact_retention
SET state = CASE WHEN quarantined_at IS NOT NULL THEN 'Quarantined' ELSE 'Declared' END,
    terminal_at = NULL,
    owner_id = NULL,
    lease_expires_at = NULL,
    attempt_count = 0,
    next_sweep_at = clock_timestamp(),
    revision = revision + 1,
    last_modified_at = clock_timestamp()
WHERE state = 'Indeterminate' AND last_error_code = 'artifact-routed-multiple-locations';
