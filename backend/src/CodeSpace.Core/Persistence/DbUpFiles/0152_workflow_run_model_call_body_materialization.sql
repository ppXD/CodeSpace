-- 0152_workflow_run_model_call_body_materialization.sql
--
-- Turns 0151's durable declarations into a database-fenced materialization queue. Provider/object-store I/O happens
-- outside transactions; these guards admit only a pristine Pending claim, an expired-lease reclaim, or settlement by
-- the exact live owner/fence. Available settlement proves the same-team immutable artifact metadata AND the atomically
-- updated call/attempt reference. Body envelopes carry an explicit format separate from WorkflowArtifact.ContentType,
-- because CAS dedup is (team,sha) and ContentType is whichever identical byte sequence was written first.

ALTER TABLE workflow_run_model_call_body_capture ADD COLUMN materialization_format VARCHAR(64) NULL;

UPDATE workflow_run_model_call_body_capture
   SET materialization_format = 'external-artifact/v1'
 WHERE state = 'Available';

ALTER TABLE workflow_run_model_call_body_capture DROP CONSTRAINT ck_workflow_run_model_call_body_capture_artifact;
ALTER TABLE workflow_run_model_call_body_capture ADD CONSTRAINT ck_workflow_run_model_call_body_capture_artifact CHECK (
    (state = 'Available' AND artifact_id IS NOT NULL AND source_sha256 ~ '^[0-9a-f]{64}$' AND size_bytes >= 0
        AND content_type IS NOT NULL AND btrim(content_type) <> ''
        AND materialization_format IN ('external-artifact/v1', 'utf8-string-envelope/v1', 'json-envelope/v1'))
    OR (state <> 'Available' AND artifact_id IS NULL AND source_sha256 IS NULL AND size_bytes IS NULL
        AND content_type IS NULL AND materialization_format IS NULL));

CREATE OR REPLACE FUNCTION workflow_run_model_call_attempt_source_guard() RETURNS TRIGGER AS $$
DECLARE
    call_source_kind        VARCHAR(64);
    call_correlation_id     UUID;
    call_node_id            VARCHAR(256);
    call_iteration_key      VARCHAR(1024);
    source_type             TEXT;
    record_correlation_id   UUID;
    source_node_id          TEXT;
    source_iteration_key    TEXT;
BEGIN
    SELECT call_row.source_kind, call_row.source_correlation_id, call_row.node_id, call_row.iteration_key
      INTO call_source_kind, call_correlation_id, call_node_id, call_iteration_key
      FROM workflow_run_model_call call_row
     WHERE call_row.id = NEW.model_call_id AND call_row.team_id = NEW.team_id
       AND call_row.workflow_run_id = NEW.workflow_run_id
     FOR KEY SHARE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'model-call source admission parent is missing or outside scope (attempt=%, call=%)', NEW.id, NEW.model_call_id;
    END IF;

    IF call_source_kind = 'workflow-run-record/v1' THEN
        IF NEW.source_terminal_record_id IS NULL OR NEW.source_evidence_revision <= 0 THEN
            RAISE EXCEPTION 'workflow-run-record projection requires terminal identity and positive revision (attempt=%)', NEW.id;
        END IF;

        SELECT source_row.record_type, source_row.correlation_id, source_row.node_id, source_row.iteration_key
          INTO source_type, record_correlation_id, source_node_id, source_iteration_key
          FROM workflow_run_record source_row
         WHERE source_row.id = NEW.source_terminal_record_id AND source_row.run_id = NEW.workflow_run_id
         FOR KEY SHARE;

        IF NOT FOUND OR source_type NOT IN ('interaction.completed', 'interaction.failed')
            OR record_correlation_id IS DISTINCT FROM call_correlation_id
            OR source_node_id IS DISTINCT FROM call_node_id
            OR source_iteration_key IS DISTINCT FROM call_iteration_key THEN
            RAISE EXCEPTION 'model-call terminal source does not exactly match its call scope (attempt=%, record=%)', NEW.id, NEW.source_terminal_record_id;
        END IF;

        IF NEW.source_started_record_id IS NOT NULL THEN
            SELECT source_row.record_type, source_row.correlation_id, source_row.node_id, source_row.iteration_key
              INTO source_type, record_correlation_id, source_node_id, source_iteration_key
              FROM workflow_run_record source_row
             WHERE source_row.id = NEW.source_started_record_id AND source_row.run_id = NEW.workflow_run_id
             FOR KEY SHARE;
            IF NOT FOUND OR source_type <> 'interaction.started'
                OR record_correlation_id IS DISTINCT FROM call_correlation_id
                OR source_node_id IS DISTINCT FROM call_node_id
                OR source_iteration_key IS DISTINCT FROM call_iteration_key THEN
                RAISE EXCEPTION 'model-call started source does not exactly match its call scope (attempt=%, record=%)', NEW.id, NEW.source_started_record_id;
            END IF;
        END IF;
    ELSIF NEW.source_started_record_id IS NOT NULL OR NEW.source_terminal_record_id IS NOT NULL OR NEW.source_evidence_revision <> 0 THEN
        RAISE EXCEPTION 'workflow-run-record ids are only valid for workflow-run-record/v1 calls (attempt=%, source=%)', NEW.id, call_source_kind;
    END IF;

    IF TG_OP = 'UPDATE' AND OLD.source_terminal_record_id IS NOT NULL THEN
        IF NEW.source_terminal_record_id IS DISTINCT FROM OLD.source_terminal_record_id THEN
            RAISE EXCEPTION 'model-call terminal source is immutable (attempt=%)', NEW.id;
        END IF;
        IF OLD.source_started_record_id IS NOT NULL AND NEW.source_started_record_id IS DISTINCT FROM OLD.source_started_record_id THEN
            RAISE EXCEPTION 'model-call started source cannot be removed or replaced (attempt=%)', NEW.id;
        END IF;
        IF OLD.source_started_record_id IS NULL AND NEW.source_started_record_id IS NOT NULL THEN
            IF NEW.source_evidence_revision <> OLD.source_evidence_revision + 1 THEN
                RAISE EXCEPTION 'late model-call source evidence must advance exactly one revision (attempt=%, old=%, new=%)', NEW.id, OLD.source_evidence_revision, NEW.source_evidence_revision;
            END IF;
            IF (to_jsonb(NEW) - ARRAY['source_started_record_id', 'source_evidence_revision', 'started_at',
                    'capture_completeness', 'last_modified_date', 'last_modified_by'])
                IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['source_started_record_id', 'source_evidence_revision', 'started_at',
                    'capture_completeness', 'last_modified_date', 'last_modified_by']) THEN
                RAISE EXCEPTION 'late model-call source admission cannot rewrite projected attempt facts (attempt=%)', NEW.id;
            END IF;
        ELSIF NEW.source_evidence_revision <> OLD.source_evidence_revision THEN
            RAISE EXCEPTION 'model-call source revision may change only with one late-start admission (attempt=%, old=%, new=%)', NEW.id, OLD.source_evidence_revision, NEW.source_evidence_revision;
        ELSIF (to_jsonb(NEW) - ARRAY['request_artifact_id', 'response_artifact_id', 'error_artifact_id',
                    'last_modified_date', 'last_modified_by'])
            IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['request_artifact_id', 'response_artifact_id', 'error_artifact_id',
                    'last_modified_date', 'last_modified_by']) THEN
            RAISE EXCEPTION 'model-call body reference update cannot rewrite projected attempt facts (attempt=%)', NEW.id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION workflow_run_model_call_body_capture_guard() RETURNS TRIGGER AS $$
DECLARE
    now_at                TIMESTAMPTZ := clock_timestamp();
    call_source_kind      VARCHAR(64);
    call_request_artifact UUID;
    attempt_team_id       UUID;
    attempt_run_id        UUID;
    attempt_call_id       UUID;
    started_record_id     UUID;
    terminal_record_id    UUID;
    response_artifact_id  UUID;
    error_artifact_id     UUID;
    source_run_id         UUID;
    source_record_type    TEXT;
    artifact_team_id      UUID;
    artifact_sha256       VARCHAR(64);
    artifact_size_bytes   BIGINT;
    artifact_content_type VARCHAR(255);
    target_artifact_id    UUID;
    settlement_owner_id   UUID;
    settlement_fence      BIGINT;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF NEW.team_id IS DISTINCT FROM OLD.team_id OR NEW.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id
            OR NEW.model_call_id IS DISTINCT FROM OLD.model_call_id OR NEW.model_call_attempt_id IS DISTINCT FROM OLD.model_call_attempt_id
            OR NEW.body_kind IS DISTINCT FROM OLD.body_kind OR NEW.source_kind IS DISTINCT FROM OLD.source_kind
            OR NEW.source_record_id IS DISTINCT FROM OLD.source_record_id OR NEW.source_property IS DISTINCT FROM OLD.source_property THEN
            RAISE EXCEPTION 'model-call body capture identity is immutable (capture=%)', OLD.id;
        END IF;
        IF OLD.state <> 'Pending' THEN
            RAISE EXCEPTION 'terminal model-call body capture is immutable (capture=%, state=%)', OLD.id, OLD.state;
        END IF;
        IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
            RAISE EXCEPTION 'model-call body capture revision/time must advance exactly once (capture=%, old=%, new=%)', OLD.id, OLD.revision, NEW.revision;
        END IF;

        IF NEW.lease_owner_id IS NOT NULL THEN
            IF OLD.lease_owner_id IS NOT NULL AND OLD.lease_expires_at > now_at THEN
                RAISE EXCEPTION 'live model-call body capture lease cannot be replaced (capture=%, fence=%)', OLD.id, OLD.lease_fence;
            END IF;
            IF OLD.next_materialization_at > now_at OR NEW.state <> 'Pending'
                OR NEW.lease_fence <> OLD.lease_fence + 1 OR NEW.lease_expires_at <= now_at
                OR NEW.materialization_attempt_count <> OLD.materialization_attempt_count + 1
                OR NEW.next_materialization_at IS DISTINCT FROM OLD.next_materialization_at
                OR NEW.last_error_code IS DISTINCT FROM OLD.last_error_code OR NEW.last_error_message IS DISTINCT FROM OLD.last_error_message
                OR NEW.terminal_at IS NOT NULL OR NEW.artifact_id IS NOT NULL OR NEW.materialization_format IS NOT NULL THEN
                RAISE EXCEPTION 'model-call body capture claim must take one fresh fence without changing outcome (capture=%)', OLD.id;
            END IF;
            RETURN NEW;
        END IF;

        settlement_owner_id := NULLIF(current_setting('codespace.workflow_run_model_call_body_lease_owner', true), '')::UUID;
        settlement_fence := NULLIF(current_setting('codespace.workflow_run_model_call_body_lease_fence', true), '')::BIGINT;
        IF OLD.lease_owner_id IS NULL OR OLD.lease_expires_at IS NULL OR OLD.lease_expires_at <= now_at
            OR settlement_owner_id IS DISTINCT FROM OLD.lease_owner_id OR settlement_fence IS DISTINCT FROM OLD.lease_fence
            OR NEW.lease_fence <> OLD.lease_fence OR NEW.materialization_attempt_count <> OLD.materialization_attempt_count THEN
            RAISE EXCEPTION 'model-call body capture settlement requires its exact live owner/fence (capture=%, fence=%)', OLD.id, OLD.lease_fence;
        END IF;
        IF NEW.state = 'Pending' THEN
            IF NEW.next_materialization_at <= now_at OR NEW.last_error_code IS NULL OR NEW.terminal_at IS NOT NULL
                OR NEW.artifact_id IS NOT NULL OR NEW.materialization_format IS NOT NULL THEN
                RAISE EXCEPTION 'model-call body capture retry must release to a future errored Pending state (capture=%)', OLD.id;
            END IF;
            RETURN NEW;
        END IF;
        IF NEW.terminal_at IS NULL OR NEW.terminal_at > NEW.last_modified_at THEN
            RAISE EXCEPTION 'model-call body capture terminal settlement requires its terminal timestamp (capture=%)', OLD.id;
        END IF;
    ELSE
        IF NEW.revision <> 1 OR NEW.materialization_attempt_count <> 0 OR NEW.lease_owner_id IS NOT NULL
            OR NEW.lease_fence <> 0 OR NEW.lease_expires_at IS NOT NULL OR NEW.last_error_code IS NOT NULL
            OR NEW.last_error_message IS NOT NULL OR NEW.state NOT IN ('Pending', 'Available') THEN
            RAISE EXCEPTION 'model-call body capture must enter as pristine Pending or admitted Available (capture=%)', NEW.id;
        END IF;
    END IF;

    SELECT call_row.source_kind, call_row.request_artifact_id
      INTO call_source_kind, call_request_artifact
      FROM workflow_run_model_call call_row
     WHERE call_row.id = NEW.model_call_id AND call_row.team_id = NEW.team_id AND call_row.workflow_run_id = NEW.workflow_run_id
     FOR KEY SHARE;

    SELECT attempt.team_id, attempt.workflow_run_id, attempt.model_call_id, attempt.source_started_record_id,
           attempt.source_terminal_record_id, attempt.response_artifact_id, attempt.error_artifact_id
      INTO attempt_team_id, attempt_run_id, attempt_call_id, started_record_id, terminal_record_id, response_artifact_id, error_artifact_id
      FROM workflow_run_model_call_attempt attempt
     WHERE attempt.id = NEW.model_call_attempt_id
     FOR KEY SHARE;

    IF call_source_kind IS DISTINCT FROM 'workflow-run-record/v1' OR attempt_team_id IS DISTINCT FROM NEW.team_id
        OR attempt_run_id IS DISTINCT FROM NEW.workflow_run_id OR attempt_call_id IS DISTINCT FROM NEW.model_call_id THEN
        RAISE EXCEPTION 'model-call body capture parent is missing or outside its exact workflow-run-record scope (capture=%)', NEW.id;
    END IF;

    SELECT source.run_id, source.record_type INTO source_run_id, source_record_type
      FROM workflow_run_record source WHERE source.id = NEW.source_record_id FOR KEY SHARE;
    IF source_run_id IS DISTINCT FROM NEW.workflow_run_id
        OR (NEW.body_kind = 'LogicalRequest' AND (NEW.source_record_id IS DISTINCT FROM started_record_id OR source_record_type <> 'interaction.started'))
        OR (NEW.body_kind = 'AttemptResponse' AND (NEW.source_record_id IS DISTINCT FROM terminal_record_id OR source_record_type <> 'interaction.completed'))
        OR (NEW.body_kind = 'AttemptError' AND (NEW.source_record_id IS DISTINCT FROM terminal_record_id OR source_record_type <> 'interaction.failed')) THEN
        RAISE EXCEPTION 'model-call body capture source is not the exact admitted attempt field (capture=%, source=%)', NEW.id, NEW.source_record_id;
    END IF;

    IF NEW.state = 'Available' THEN
        SELECT artifact.team_id, artifact.sha256, artifact.size_bytes, artifact.content_type
          INTO artifact_team_id, artifact_sha256, artifact_size_bytes, artifact_content_type
          FROM workflow_artifact artifact WHERE artifact.id = NEW.artifact_id FOR KEY SHARE;
        target_artifact_id := CASE NEW.body_kind
            WHEN 'LogicalRequest' THEN call_request_artifact
            WHEN 'AttemptResponse' THEN response_artifact_id
            WHEN 'AttemptError' THEN error_artifact_id
        END;
        IF artifact_team_id IS DISTINCT FROM NEW.team_id OR NEW.source_sha256 IS DISTINCT FROM artifact_sha256
            OR NEW.size_bytes IS DISTINCT FROM artifact_size_bytes OR NEW.content_type IS DISTINCT FROM artifact_content_type
            OR target_artifact_id IS DISTINCT FROM NEW.artifact_id THEN
            RAISE EXCEPTION 'Available body capture requires exact same-team artifact metadata and target reference (capture=%, artifact=%)', NEW.id, NEW.artifact_id;
        END IF;
        IF NEW.materialization_format IN ('utf8-string-envelope/v1', 'json-envelope/v1')
            AND (artifact_content_type <> 'application/vnd.codespace.workflow-model-call-body' OR artifact_size_bytes < 8) THEN
            RAISE EXCEPTION 'enveloped body capture requires the typed envelope artifact contract (capture=%, artifact=%)', NEW.id, NEW.artifact_id;
        END IF;
        IF TG_OP = 'INSERT' AND (NEW.body_kind <> 'AttemptResponse' OR NEW.materialization_format <> 'external-artifact/v1') THEN
            RAISE EXCEPTION 'initial Available body capture may only adopt its already-admitted external response artifact (capture=%)', NEW.id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON COLUMN workflow_run_model_call_body_capture.materialization_format IS
    'Body-level decoding identity. Never inferred from WorkflowArtifact.ContentType because CAS dedup retains the first writer metadata.';
