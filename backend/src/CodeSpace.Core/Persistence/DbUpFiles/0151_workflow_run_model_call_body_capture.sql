-- 0151_workflow_run_model_call_body_capture.sql
--
-- Transactional metadata outbox for legacy interaction-tape bodies. The projector only declares exact immutable
-- source fields here; it never performs artifact I/O while holding its run lock/transaction. A later leased worker can
-- retry storage without losing the source, and old projected rows remain discoverable through the same anti-join.
-- This plane is telemetry-only: it changes no model execution, completion assessment, or terminal authority path.

CREATE TABLE workflow_run_model_call_body_capture (
    id                            UUID         NOT NULL PRIMARY KEY,
    team_id                       UUID         NOT NULL,
    workflow_run_id               UUID         NOT NULL,
    model_call_id                 UUID         NOT NULL,
    model_call_attempt_id         UUID         NOT NULL,
    body_kind                     VARCHAR(32)  NOT NULL,
    source_kind                   VARCHAR(64)  NOT NULL,
    source_record_id              UUID         NOT NULL,
    source_property               VARCHAR(32)  NOT NULL,
    state                         VARCHAR(32)  NOT NULL,
    artifact_id                   UUID         NULL,
    source_sha256                 VARCHAR(64)  NULL,
    size_bytes                    BIGINT       NULL,
    content_type                  VARCHAR(255) NULL,
    materialization_attempt_count INTEGER      NOT NULL,
    next_materialization_at       TIMESTAMPTZ  NOT NULL,
    lease_owner_id                UUID         NULL,
    lease_fence                   BIGINT       NOT NULL,
    lease_expires_at              TIMESTAMPTZ  NULL,
    last_error_code               VARCHAR(128) NULL,
    last_error_message            VARCHAR(2048) NULL,
    revision                      BIGINT       NOT NULL,
    created_at                    TIMESTAMPTZ  NOT NULL,
    last_modified_at              TIMESTAMPTZ  NOT NULL,
    terminal_at                   TIMESTAMPTZ  NULL,

    CONSTRAINT fk_workflow_run_model_call_body_capture_call FOREIGN KEY (model_call_id, team_id, workflow_run_id)
        REFERENCES workflow_run_model_call (id, team_id, workflow_run_id) ON DELETE CASCADE,
    CONSTRAINT fk_workflow_run_model_call_body_capture_attempt FOREIGN KEY (model_call_attempt_id)
        REFERENCES workflow_run_model_call_attempt (id) ON DELETE CASCADE,
    CONSTRAINT fk_workflow_run_model_call_body_capture_source FOREIGN KEY (source_record_id)
        REFERENCES workflow_run_record (id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_model_call_body_capture_artifact CHECK (
        (state = 'Available' AND artifact_id IS NOT NULL AND source_sha256 ~ '^[0-9a-f]{64}$'
            AND size_bytes >= 0 AND content_type IS NOT NULL AND btrim(content_type) <> '')
        OR (state <> 'Available' AND artifact_id IS NULL AND source_sha256 IS NULL AND size_bytes IS NULL AND content_type IS NULL)),
    CONSTRAINT ck_workflow_run_model_call_body_capture_claim CHECK (
        lease_fence >= 0 AND materialization_attempt_count >= 0
        AND ((lease_owner_id IS NULL AND lease_expires_at IS NULL)
            OR (lease_owner_id IS NOT NULL AND lease_fence > 0 AND lease_expires_at IS NOT NULL))),
    CONSTRAINT ck_workflow_run_model_call_body_capture_error CHECK (
        (last_error_code IS NULL AND last_error_message IS NULL)
        OR (last_error_code IS NOT NULL AND btrim(last_error_code) <> '')),
    CONSTRAINT ck_workflow_run_model_call_body_capture_identity CHECK (
        source_kind = 'workflow-run-record/v1'
        AND ((body_kind = 'LogicalRequest' AND source_property = 'prompt')
            OR (body_kind = 'AttemptResponse' AND source_property = 'output')
            OR (body_kind = 'AttemptError' AND source_property = 'error'))),
    CONSTRAINT ck_workflow_run_model_call_body_capture_state CHECK (
        state IN ('Pending', 'Available', 'NotRecorded', 'Corrupt', 'CaptureFailed', 'ExternalStateIndeterminate')
        AND ((state = 'Pending' AND terminal_at IS NULL)
            OR (state <> 'Pending' AND terminal_at IS NOT NULL AND lease_owner_id IS NULL))),
    CONSTRAINT ck_workflow_run_model_call_body_capture_time CHECK (
        revision > 0 AND next_materialization_at >= created_at AND last_modified_at >= created_at
        AND (terminal_at IS NULL OR last_modified_at >= terminal_at))
);

CREATE UNIQUE INDEX ux_workflow_run_model_call_body_capture_identity
    ON workflow_run_model_call_body_capture (model_call_attempt_id, body_kind);

CREATE INDEX ix_workflow_run_model_call_attempt_body_capture
    ON workflow_run_model_call_attempt (created_date, id) WHERE source_terminal_record_id IS NOT NULL;

CREATE INDEX ix_workflow_run_model_call_body_capture_pending
    ON workflow_run_model_call_body_capture (next_materialization_at, team_id, id)
    INCLUDE (lease_expires_at, lease_fence) WHERE state = 'Pending';

CREATE INDEX ix_workflow_run_model_call_body_capture_artifact
    ON workflow_run_model_call_body_capture (team_id, artifact_id, id) WHERE artifact_id IS NOT NULL;

CREATE OR REPLACE FUNCTION workflow_run_model_call_body_capture_guard() RETURNS TRIGGER AS $$
DECLARE
    call_source_kind      VARCHAR(64);
    attempt_team_id       UUID;
    attempt_run_id        UUID;
    attempt_call_id       UUID;
    started_record_id     UUID;
    terminal_record_id    UUID;
    response_artifact_id  UUID;
    source_run_id         UUID;
    source_record_type    TEXT;
    artifact_team_id      UUID;
    artifact_sha256       VARCHAR(64);
    artifact_size_bytes   BIGINT;
    artifact_content_type VARCHAR(255);
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF NEW.team_id IS DISTINCT FROM OLD.team_id
            OR NEW.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id
            OR NEW.model_call_id IS DISTINCT FROM OLD.model_call_id
            OR NEW.model_call_attempt_id IS DISTINCT FROM OLD.model_call_attempt_id
            OR NEW.body_kind IS DISTINCT FROM OLD.body_kind
            OR NEW.source_kind IS DISTINCT FROM OLD.source_kind
            OR NEW.source_record_id IS DISTINCT FROM OLD.source_record_id
            OR NEW.source_property IS DISTINCT FROM OLD.source_property THEN
            RAISE EXCEPTION 'model-call body capture identity is immutable (capture=%)', OLD.id;
        END IF;
        IF NEW.revision <> OLD.revision + 1 THEN
            RAISE EXCEPTION 'model-call body capture revision must advance exactly once (capture=%, old=%, new=%)', OLD.id, OLD.revision, NEW.revision;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.revision <> 1 OR NEW.materialization_attempt_count <> 0 OR NEW.lease_owner_id IS NOT NULL
        OR NEW.lease_fence <> 0 OR NEW.lease_expires_at IS NOT NULL OR NEW.last_error_code IS NOT NULL
        OR NEW.last_error_message IS NOT NULL OR NEW.state NOT IN ('Pending', 'Available') THEN
        RAISE EXCEPTION 'model-call body capture must enter as pristine Pending or admitted Available (capture=%)', NEW.id;
    END IF;

    SELECT call_row.source_kind
      INTO call_source_kind
      FROM workflow_run_model_call call_row
     WHERE call_row.id = NEW.model_call_id AND call_row.team_id = NEW.team_id
       AND call_row.workflow_run_id = NEW.workflow_run_id
     FOR KEY SHARE;

    SELECT attempt.team_id, attempt.workflow_run_id, attempt.model_call_id, attempt.source_started_record_id,
           attempt.source_terminal_record_id, attempt.response_artifact_id
      INTO attempt_team_id, attempt_run_id, attempt_call_id, started_record_id, terminal_record_id, response_artifact_id
      FROM workflow_run_model_call_attempt attempt
     WHERE attempt.id = NEW.model_call_attempt_id
     FOR KEY SHARE;

    IF call_source_kind IS DISTINCT FROM 'workflow-run-record/v1'
        OR attempt_team_id IS DISTINCT FROM NEW.team_id
        OR attempt_run_id IS DISTINCT FROM NEW.workflow_run_id
        OR attempt_call_id IS DISTINCT FROM NEW.model_call_id THEN
        RAISE EXCEPTION 'model-call body capture parent is missing or outside its exact workflow-run-record scope (capture=%)', NEW.id;
    END IF;

    SELECT source.run_id, source.record_type
      INTO source_run_id, source_record_type
      FROM workflow_run_record source
     WHERE source.id = NEW.source_record_id
     FOR KEY SHARE;

    IF source_run_id IS DISTINCT FROM NEW.workflow_run_id
        OR (NEW.body_kind = 'LogicalRequest'
            AND (NEW.source_record_id IS DISTINCT FROM started_record_id OR source_record_type <> 'interaction.started'))
        OR (NEW.body_kind = 'AttemptResponse'
            AND (NEW.source_record_id IS DISTINCT FROM terminal_record_id OR source_record_type <> 'interaction.completed'))
        OR (NEW.body_kind = 'AttemptError'
            AND (NEW.source_record_id IS DISTINCT FROM terminal_record_id OR source_record_type <> 'interaction.failed')) THEN
        RAISE EXCEPTION 'model-call body capture source is not the exact admitted attempt field (capture=%, source=%)', NEW.id, NEW.source_record_id;
    END IF;

    IF NEW.state = 'Available' THEN
        SELECT artifact.team_id, artifact.sha256, artifact.size_bytes, artifact.content_type
          INTO artifact_team_id, artifact_sha256, artifact_size_bytes, artifact_content_type
          FROM workflow_artifact artifact
         WHERE artifact.id = NEW.artifact_id
         FOR KEY SHARE;
        IF NEW.body_kind <> 'AttemptResponse' OR NEW.artifact_id IS DISTINCT FROM response_artifact_id
            OR artifact_team_id IS DISTINCT FROM NEW.team_id OR NEW.source_sha256 IS DISTINCT FROM artifact_sha256
            OR NEW.size_bytes IS DISTINCT FROM artifact_size_bytes OR NEW.content_type IS DISTINCT FROM artifact_content_type THEN
            RAISE EXCEPTION 'initial Available body capture requires the exact same-team response artifact (capture=%, artifact=%)', NEW.id, NEW.artifact_id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_model_call_body_capture_enforce
    BEFORE INSERT OR UPDATE ON workflow_run_model_call_body_capture
    FOR EACH ROW EXECUTE FUNCTION workflow_run_model_call_body_capture_guard();

ALTER TABLE workflow_run_capture_gap DROP CONSTRAINT ck_workflow_run_capture_gap_resolution;
ALTER TABLE workflow_run_capture_gap ADD CONSTRAINT ck_workflow_run_capture_gap_resolution CHECK (
    (resolution = 'Open' AND recovered_at IS NULL AND recovered_by_kind IS NULL AND recovered_by_id IS NULL)
    OR (resolution = 'Recovered' AND recovered_at IS NOT NULL AND recovered_at >= noticed_at
        AND recovered_by_kind IS NOT NULL AND recovered_by_kind IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'capture-gap', 'data-manifest')
        AND recovered_by_id IS NOT NULL AND btrim(recovered_by_id) <> ''));

ALTER TABLE workflow_run_capture_gap DROP CONSTRAINT ck_workflow_run_capture_gap_subject;
ALTER TABLE workflow_run_capture_gap ADD CONSTRAINT ck_workflow_run_capture_gap_subject CHECK (
    subject_kind IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'capture-gap', 'data-manifest')
    AND (subject_id IS NULL OR btrim(subject_id) <> '') AND btrim(capture_source) <> '');

ALTER TABLE workflow_run_data_manifest DROP CONSTRAINT ck_workflow_run_data_manifest_facet;
ALTER TABLE workflow_run_data_manifest ADD CONSTRAINT ck_workflow_run_data_manifest_facet CHECK (
    facet IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'capture-gap', 'data-manifest'));

COMMENT ON TABLE workflow_run_model_call_body_capture IS
    'Retryable metadata outbox from exact workflow-run-record fields to model-call body artifacts; telemetry-only.';
COMMENT ON COLUMN workflow_run_model_call_body_capture.source_record_id IS
    'Exact immutable source row; never replaced by correlation fallback or another producer.';
COMMENT ON COLUMN workflow_run_model_call_body_capture.next_materialization_at IS
    'Leased worker schedule. Projector performs no artifact I/O and therefore cannot consume this source on storage failure.';
