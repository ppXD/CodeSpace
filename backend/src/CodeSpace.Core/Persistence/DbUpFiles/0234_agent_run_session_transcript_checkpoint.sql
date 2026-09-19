-- 0234_agent_run_session_transcript_checkpoint.sql
--
-- A run whose launch host dies is NOT continued by another host, because nothing it produced mid-run is durable.
--
-- The resumable CLI session transcript — the file the harness's own `--resume` reads — is captured exactly once, at
-- run END, from the LAUNCHING host's spool (AgentRunExecutor.CaptureSessionTranscriptAsync reads
-- LocalProcessRunner.ConfigHomePath(handle.SpoolDirectory)). When the host holding that spool is gone, the file is
-- gone with it: AgentRunReconcilerService abandons the run (a foreign handle is left alone until its own deadline,
-- then failed) and every reader is left with a cold restart as the only recovery.
--
-- The retry itself already happens: the reconciler abandons the run, the agent.run node grades that as a retryable
-- failure, and the node's retry policy stages a fresh agent. It is the CONVERSATION that is lost, so the retry is
-- cold. These columns are what make it warm. The observer's own drain tick uploads the live transcript to the
-- artifact store and stamps the reference here; the reconciler's abandon leaves it on the row (where the executor's
-- own terminal write clears it), the completion notifier projects it across the wait boundary, and the node's
-- respawn stamps it onto the fresh attempt's task.
--
-- Shape, and why each part of it:
--
--   * NULLABLE with NO DEFAULT. agent_run is a hot table; a nullable ADD COLUMN with no default is catalog-only in
--     PostgreSQL (no table rewrite, no long ACCESS EXCLUSIVE hold), which a DEFAULT would not be on every supported
--     server. NULL is also the escape for the rollback direction: an older binary that has never heard of these
--     columns simply does not select them, and a row they were never written to reads as "no checkpoint" — which is
--     precisely the pre-3c behaviour, a cold start.
--
--   * The artifact id is a SOFT link, like every other *_artifact_id in this schema — no FK to workflow_artifact.
--     It is registered in ArtifactReferenceOracle.ReferenceSites (test-pinned, and cross-checked against the EF model
--     by ArtifactReferenceOracleTests) so the reaper can never collect a checkpoint a live run still names.
--
--   * A PARTIAL INDEX on that soft link, for the reason 0144 gives for the ten it added with the oracle: the oracle
--     asks "does ANY row anywhere still reference this artifact id" as one EXISTS per site, and an unindexed site on
--     a run-scale table makes that EXISTS a sequential scan of agent_run on every artifact the reaper claims. Partial
--     on IS NOT NULL because a checkpoint is the exception, so the index stays small on the hot table. It also builds
--     instantly here: the column is NULL on every existing row, so there is nothing to insert into it.
--
-- LOCKS, stated as 0144 states them: DbUp runs the whole upgrade in ONE transaction, so the CREATE INDEX below holds
-- its SHARE lock on agent_run (blocking writes, not reads) until that transaction commits. CREATE INDEX CONCURRENTLY
-- cannot run inside a transaction, so shortening that window is a change to DbUpRunner, not to this file.
--
--   * A THIRD column, resumed_from_agent_run_id, so a continuation NAMES the run it took over from in a column
--     rather than only in the sentence an event carries. Same shape as every other agent_run cross-reference: a soft
--     link with no FK (agent runs are managed independently of one another), nullable, no default, no index — nothing
--     queries by it yet, and adding an index for a reader that does not exist would cost every agent_run write. It is
--     NOT an artifact id, so the reference oracle correctly does not probe it.
--
-- Rollback: ALTER TABLE agent_run DROP COLUMN resumed_from_agent_run_id, DROP COLUMN
-- session_transcript_checkpoint_at, DROP COLUMN session_transcript_checkpoint_artifact_id (the index goes with the
-- column); the stored transcripts then age out under their own retention declaration
-- (ArtifactRetentionClass.SessionTranscriptCheckpoint) once nothing references them.

ALTER TABLE agent_run
    ADD COLUMN session_transcript_checkpoint_artifact_id uuid NULL,
    ADD COLUMN session_transcript_checkpoint_at timestamptz NULL,
    ADD COLUMN resumed_from_agent_run_id uuid NULL;

CREATE INDEX ix_agent_run_session_transcript_checkpoint_artifact
    ON agent_run (session_transcript_checkpoint_artifact_id)
    WHERE session_transcript_checkpoint_artifact_id IS NOT NULL;

COMMENT ON COLUMN agent_run.session_transcript_checkpoint_artifact_id IS
    'Soft link to workflow_artifact.id holding this run''s resumable session transcript as of its most recent mid-run checkpoint — what another host continues from when this run''s host dies. NULL until the first checkpoint.';

COMMENT ON COLUMN agent_run.session_transcript_checkpoint_at IS
    'When session_transcript_checkpoint_artifact_id was taken: the cadence clock for the next checkpoint, and the age that says how much of the conversation survived the lost host.';

COMMENT ON COLUMN agent_run.resumed_from_agent_run_id IS
    'The abandoned agent run this one succeeded after a host loss, and was staged to continue from its session checkpoint. Whether that checkpoint could be read is only known at launch, so this records succession, not recovery. Soft link (no FK, like every other agent_run cross-reference); NULL for every ordinary run.';
