-- 0144_workflow_artifact_retention.sql
--
-- Gives the artifact plane its FIRST retention path. Until now nothing ever deleted a workflow_artifact row: 0016
-- shipped the table with an immutability trigger whose DELETE arm is gated on a session variable and a header that
-- called cleanup "a future operator-controlled job". This is that job's ledger.
--
-- WHY A LEDGER AND NOT A SCAN. Before this file no foreign key anywhere pointed at workflow_artifact, and the one added
-- below points the other way (this ledger's own row at the artifact it declares). Every REFERENCE to an artifact is
-- still a soft link, and the soft links do not all live in columns. They also live inside JSON
-- (workflow_run_record.payload_json carries {"$artifact_ref":{"id":…}} and {"$artifact_id":…}; agent_run.result_jsonb
-- carries the transcript refs; workflow_run.outputs_jsonb can carry a ref) and inside artifact CONTENT itself
-- (AgentRunExecutor.PutPublishEvidenceAsync serializes patchArtifactId into an evidence artifact's bytes;
-- CompletionAssessmentComposer serializes contentArtifactId into another). A reaper that decided "unreferenced" by
-- scanning would have to be complete over all of that, and being wrong once is silent unrecoverable data loss.
--
-- So candidacy is POSITIVE, never inferred: only an artifact whose producer declared it here is ever a candidate, and
-- a declaration is minted only by IArtifactRetentionWriter.PutDeclaredAsync, only when that call is the write that
-- INSERTED the row (never a dedup hit), and only for a class whose complete reference set is enumerable in columns.
-- Every artifact without a row here — every row that predates this migration, and every byte written by the JSON
-- offload paths — is invisible to the reaper. Fail-closed is the default state, not a check the reaper performs.
--
-- The content-addressed store deduplicates on (team_id, sha256), so a later writer of the same bytes gets the same
-- id and may reference it from anywhere, including the JSON planes this ledger cannot see. That is why the store
-- REVOKES the declaration on every dedup hit (ArtifactStore.FindDedupTargetAsync) and why 'Revoked' is terminal: the
-- moment a second writer touches the bytes, this ledger stops claiming to enumerate their references.
--
-- Rollback: DROP TABLE workflow_artifact_retention; then drop the ten reference-site indexes added below.

-- LOCKS, stated honestly: DbUp runs the entire upgrade inside ONE transaction (DbUpRunner.BuildEngine calls
-- .WithTransaction()), and Postgres releases no lock before that transaction commits. Each CREATE INDEX below takes a
-- SHARE lock on its table, which blocks writes to that table — so this file blocks INSERTs into agent_run_event,
-- artifact_manifest, publish_manifest, workflow_run_model_call(_attempt) and workflow_run_tool_call(_attempt) for the
-- length of the whole upgrade run. CREATE INDEX CONCURRENTLY cannot run inside a transaction, so shortening this
-- window is a change to DbUpRunner, not to this file. The partial predicates below keep the resulting indexes small
-- (the referencing columns are NULL on the overwhelming majority of rows) but do NOT shorten the build scan.

CREATE TABLE workflow_artifact_retention (
    -- One row per DECLARED artifact. PK is the artifact, so a declaration is at-most-once per artifact and the second
    -- declarer's ON CONFLICT DO NOTHING is the whole concurrency story for minting.
    artifact_id         UUID PRIMARY KEY REFERENCES workflow_artifact (id) ON DELETE CASCADE,
    team_id             UUID NOT NULL REFERENCES team (id),

    -- The producer's retention class. Text, not an enum type, so adding a class is application code plus a policy
    -- constant (ArtifactRetentionPolicy) rather than a migration. An UNREGISTERED class reads as Indeterminate and is
    -- therefore KEPT, so a rollback that removes a class from the policy cannot turn its rows into deletions.
    retention_class     TEXT NOT NULL,

    -- The holder the producer said it was about to write, carried for diagnosis of a collection after the fact. The
    -- reaper does NOT trust it as the reference check — ArtifactReferenceOracle queries every reference site.
    holder_kind         TEXT NOT NULL,
    holder_id           UUID NOT NULL,

    state               TEXT NOT NULL,

    declared_at         TIMESTAMPTZ NOT NULL,

    -- Fair-queue key: the earliest DB-clock instant this row may be claimed again. Retry backoff moves it forward.
    next_sweep_at       TIMESTAMPTZ NOT NULL,

    -- When the reaper first observed this artifact unreferenced. Collection requires that the quarantine window has
    -- since elapsed, so a just-written artifact whose reference has not yet committed survives its first sweep.
    quarantined_at      TIMESTAMPTZ NULL,
    terminal_at         TIMESTAMPTZ NULL,

    -- Claim/fence, the same shape agent_run_log_capture_intent uses: a bounded lease with an owner and a monotonic
    -- fence so a settlement can prove it is the exact claim that did the work.
    owner_id            UUID NULL,
    fence_epoch         BIGINT NOT NULL DEFAULT 0,
    attempt_count       INTEGER NOT NULL DEFAULT 0,
    lease_expires_at    TIMESTAMPTZ NULL,

    last_error_code     TEXT NULL,
    last_error_message  TEXT NULL,

    revision            BIGINT NOT NULL DEFAULT 1,
    last_modified_at    TIMESTAMPTZ NOT NULL,

    -- 'Declared' and 'Quarantined' are the only live states. 'Referenced' (the declared reference landed), 'Revoked'
    -- (a second writer touched the bytes, or a producer withdrew) and 'Indeterminate' (the reference status could not
    -- be established within the retry budget) are all terminal and all mean KEEP FOREVER. There is deliberately no
    -- terminal state that means "delete later": collection deletes the artifact, and this row goes with it via the
    -- cascade above.
    CONSTRAINT ck_workflow_artifact_retention_state CHECK (
        state IN ('Declared', 'Quarantined', 'Referenced', 'Revoked', 'Indeterminate')
        AND ((state IN ('Referenced', 'Revoked', 'Indeterminate') AND terminal_at IS NOT NULL AND owner_id IS NULL AND lease_expires_at IS NULL)
             OR (state IN ('Declared', 'Quarantined') AND terminal_at IS NULL))
        AND (state <> 'Quarantined' OR quarantined_at IS NOT NULL)
        AND (state <> 'Declared' OR quarantined_at IS NULL)
    ),

    CONSTRAINT ck_workflow_artifact_retention_claim CHECK (
        fence_epoch >= 0 AND attempt_count >= 0
        AND ((owner_id IS NULL AND lease_expires_at IS NULL) OR (owner_id IS NOT NULL AND lease_expires_at IS NOT NULL AND fence_epoch > 0))
    ),

    CONSTRAINT ck_workflow_artifact_retention_identity CHECK (
        btrim(retention_class) <> '' AND btrim(holder_kind) <> ''
        AND holder_id <> '00000000-0000-0000-0000-000000000000'::uuid
    ),

    CONSTRAINT ck_workflow_artifact_retention_time CHECK (
        revision > 0
        AND next_sweep_at >= declared_at
        AND last_modified_at >= declared_at
        AND (quarantined_at IS NULL OR quarantined_at >= declared_at)
        AND (terminal_at IS NULL OR terminal_at >= declared_at)
    ),

    CONSTRAINT ck_workflow_artifact_retention_error CHECK (
        (last_error_code IS NULL AND last_error_message IS NULL) OR (last_error_code IS NOT NULL AND btrim(last_error_code) <> '')
    )
);

-- The reaper's claim query reads the per-team head of the live queue. Partial on the live states so the terminal rows
-- (which are the steady state — every settled declaration ends terminal) stay out of it entirely.
CREATE INDEX ix_workflow_artifact_retention_sweep
    ON workflow_artifact_retention (team_id, next_sweep_at, artifact_id)
    WHERE state IN ('Declared', 'Quarantined');

COMMENT ON TABLE workflow_artifact_retention IS
    'Positive retention declarations over workflow_artifact. A row here is the ONLY way an artifact becomes a reap '
    'candidate; an artifact with no row is never deleted. Minted only by the write that inserted the artifact row, '
    'revoked on any later dedup hit for the same bytes. Deleted with its artifact via ON DELETE CASCADE.';

COMMENT ON COLUMN workflow_artifact_retention.retention_class IS
    'The producer class whose complete reference set ArtifactReferenceOracle can enumerate in columns. A class the '
    'running policy does not register reads as Indeterminate, which keeps the artifact.';

-- ─── Reference-site indexes ───────────────────────────────────────────────────
-- ArtifactReferenceOracle asks "does ANY row anywhere still reference this artifact id" as one EXISTS per site. These
-- are the sites, found by enumerating every column in the schema whose name ends in artifact_id; without an index each
-- EXISTS is a sequential scan of a run-scale table. Partial on IS NOT NULL because a reference is the exception.

CREATE INDEX ix_artifact_manifest_content_artifact
    ON artifact_manifest (content_artifact_id);

CREATE INDEX ix_publish_manifest_patch_artifact
    ON publish_manifest (patch_artifact_id)
    WHERE patch_artifact_id IS NOT NULL;

CREATE INDEX ix_agent_run_event_data_artifact
    ON agent_run_event (data_artifact_id)
    WHERE data_artifact_id IS NOT NULL;

CREATE INDEX ix_workflow_run_model_call_request_artifact
    ON workflow_run_model_call (request_artifact_id)
    WHERE request_artifact_id IS NOT NULL;

CREATE INDEX ix_workflow_run_model_call_attempt_request_artifact
    ON workflow_run_model_call_attempt (request_artifact_id)
    WHERE request_artifact_id IS NOT NULL;

CREATE INDEX ix_workflow_run_model_call_attempt_response_artifact
    ON workflow_run_model_call_attempt (response_artifact_id)
    WHERE response_artifact_id IS NOT NULL;

CREATE INDEX ix_workflow_run_model_call_attempt_error_artifact
    ON workflow_run_model_call_attempt (error_artifact_id)
    WHERE error_artifact_id IS NOT NULL;

CREATE INDEX ix_workflow_run_tool_call_arguments_artifact
    ON workflow_run_tool_call (arguments_artifact_id)
    WHERE arguments_artifact_id IS NOT NULL;

CREATE INDEX ix_workflow_run_tool_call_attempt_result_artifact
    ON workflow_run_tool_call_attempt (result_artifact_id)
    WHERE result_artifact_id IS NOT NULL;

CREATE INDEX ix_workflow_run_tool_call_attempt_error_artifact
    ON workflow_run_tool_call_attempt (error_artifact_id)
    WHERE error_artifact_id IS NOT NULL;
