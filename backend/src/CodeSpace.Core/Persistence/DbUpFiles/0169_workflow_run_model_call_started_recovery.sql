-- 0169_workflow_run_model_call_started_recovery.sql
-- Makes a durable interaction.started fact independently projectable. A process death after provider dispatch can
-- therefore remain visible as a Pending/Partial model-call attempt, and an eventually committed terminal fact can
-- advance that same attempt exactly once instead of creating a second logical call.

ALTER TABLE workflow_run_model_call_attempt
    DROP CONSTRAINT ck_workflow_run_model_call_attempt_source_identity,
    ADD CONSTRAINT ck_workflow_run_model_call_attempt_source_identity CHECK (
        (source_started_record_id IS NULL AND source_terminal_record_id IS NULL AND source_evidence_revision = 0)
        OR ((source_started_record_id IS NOT NULL OR source_terminal_record_id IS NOT NULL) AND source_evidence_revision > 0)
    );

CREATE INDEX ix_workflow_run_model_call_attempt_late_terminal
    ON workflow_run_model_call_attempt (workflow_run_id, model_call_id)
    WHERE source_started_record_id IS NOT NULL AND source_terminal_record_id IS NULL;

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
     WHERE call_row.id = NEW.model_call_id
       AND call_row.team_id = NEW.team_id
       AND call_row.workflow_run_id = NEW.workflow_run_id
     FOR KEY SHARE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'model-call source admission parent is missing or outside scope (attempt=%, call=%)', NEW.id, NEW.model_call_id;
    END IF;

    IF call_source_kind = 'workflow-run-record/v1' THEN
        IF (NEW.source_started_record_id IS NULL AND NEW.source_terminal_record_id IS NULL) OR NEW.source_evidence_revision <= 0 THEN
            RAISE EXCEPTION 'workflow-run-record projection requires source identity and positive revision (attempt=%)', NEW.id;
        END IF;

        IF NEW.source_terminal_record_id IS NOT NULL THEN
            SELECT source_row.record_type, source_row.correlation_id, source_row.node_id, source_row.iteration_key
              INTO source_type, record_correlation_id, source_node_id, source_iteration_key
              FROM workflow_run_record source_row
             WHERE source_row.id = NEW.source_terminal_record_id AND source_row.run_id = NEW.workflow_run_id
             FOR KEY SHARE;

            IF NOT FOUND OR source_type NOT IN ('interaction.completed', 'interaction.failed')
                OR record_correlation_id IS DISTINCT FROM call_correlation_id OR source_node_id IS DISTINCT FROM call_node_id
                OR source_iteration_key IS DISTINCT FROM call_iteration_key THEN
                RAISE EXCEPTION 'model-call terminal source does not exactly match its call scope (attempt=%, record=%)', NEW.id, NEW.source_terminal_record_id;
            END IF;
        END IF;

        IF NEW.source_started_record_id IS NOT NULL THEN
            SELECT source_row.record_type, source_row.correlation_id, source_row.node_id, source_row.iteration_key
              INTO source_type, record_correlation_id, source_node_id, source_iteration_key
              FROM workflow_run_record source_row
             WHERE source_row.id = NEW.source_started_record_id AND source_row.run_id = NEW.workflow_run_id
             FOR KEY SHARE;

            IF NOT FOUND OR source_type <> 'interaction.started'
                OR record_correlation_id IS DISTINCT FROM call_correlation_id OR source_node_id IS DISTINCT FROM call_node_id
                OR source_iteration_key IS DISTINCT FROM call_iteration_key THEN
                RAISE EXCEPTION 'model-call started source does not exactly match its call scope (attempt=%, record=%)', NEW.id, NEW.source_started_record_id;
            END IF;
        END IF;
    ELSIF NEW.source_started_record_id IS NOT NULL OR NEW.source_terminal_record_id IS NOT NULL OR NEW.source_evidence_revision <> 0 THEN
        RAISE EXCEPTION 'workflow-run-record ids are only valid for workflow-run-record/v1 calls (attempt=%, source=%)', NEW.id, call_source_kind;
    END IF;

    IF TG_OP = 'UPDATE' AND call_source_kind = 'workflow-run-record/v1' THEN
        IF OLD.source_terminal_record_id IS NOT NULL AND NEW.source_terminal_record_id IS DISTINCT FROM OLD.source_terminal_record_id THEN
            RAISE EXCEPTION 'model-call terminal source cannot be removed or replaced (attempt=%)', NEW.id;
        END IF;
        IF OLD.source_started_record_id IS NOT NULL AND NEW.source_started_record_id IS DISTINCT FROM OLD.source_started_record_id THEN
            RAISE EXCEPTION 'model-call started source cannot be removed or replaced (attempt=%)', NEW.id;
        END IF;

        IF OLD.source_started_record_id IS NULL AND NEW.source_started_record_id IS NOT NULL THEN
            IF OLD.source_terminal_record_id IS NULL OR NEW.source_terminal_record_id IS DISTINCT FROM OLD.source_terminal_record_id THEN
                RAISE EXCEPTION 'late model-call start requires one existing immutable terminal source (attempt=%)', NEW.id;
            END IF;
            IF NEW.source_evidence_revision <> OLD.source_evidence_revision + 1 THEN
                RAISE EXCEPTION 'late model-call start evidence must advance exactly one revision (attempt=%, old=%, new=%)', NEW.id, OLD.source_evidence_revision, NEW.source_evidence_revision;
            END IF;
            IF (to_jsonb(NEW) - ARRAY['source_started_record_id', 'source_evidence_revision', 'started_at',
                    'capture_completeness', 'last_modified_date', 'last_modified_by'])
                IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['source_started_record_id', 'source_evidence_revision', 'started_at',
                    'capture_completeness', 'last_modified_date', 'last_modified_by']) THEN
                RAISE EXCEPTION 'late model-call start admission cannot rewrite projected attempt facts (attempt=%)', NEW.id;
            END IF;
        ELSIF OLD.source_terminal_record_id IS NULL AND NEW.source_terminal_record_id IS NOT NULL THEN
            IF OLD.source_started_record_id IS NULL OR NEW.source_started_record_id IS DISTINCT FROM OLD.source_started_record_id THEN
                RAISE EXCEPTION 'late model-call terminal requires one existing immutable started source (attempt=%)', NEW.id;
            END IF;
            IF NEW.source_evidence_revision <> OLD.source_evidence_revision + 1 THEN
                RAISE EXCEPTION 'late model-call terminal evidence must advance exactly one revision (attempt=%, old=%, new=%)', NEW.id, OLD.source_evidence_revision, NEW.source_evidence_revision;
            END IF;
            IF (to_jsonb(NEW) - ARRAY['source_terminal_record_id', 'source_evidence_revision', 'effective_provider',
                    'effective_model', 'response_artifact_id', 'status', 'error_code', 'finish_reason',
                    'capture_completeness', 'input_tokens', 'output_tokens', 'completed_at', 'last_modified_date',
                    'last_modified_by'])
                IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['source_terminal_record_id', 'source_evidence_revision',
                    'effective_provider', 'effective_model', 'response_artifact_id', 'status', 'error_code',
                    'finish_reason', 'capture_completeness', 'input_tokens', 'output_tokens', 'completed_at',
                    'last_modified_date', 'last_modified_by']) THEN
                RAISE EXCEPTION 'late model-call terminal admission cannot rewrite unrelated attempt facts (attempt=%)', NEW.id;
            END IF;
        ELSIF OLD.source_started_record_id IS NOT NULL AND OLD.source_terminal_record_id IS NULL
            AND NEW.source_started_record_id IS NOT DISTINCT FROM OLD.source_started_record_id
            AND NEW.source_terminal_record_id IS NULL AND OLD.status = 'Pending' AND NEW.status = 'Indeterminate'
            AND NEW.error_code = 'TerminalCaptureMissing' AND NEW.completed_at IS NOT NULL THEN
            IF NEW.source_evidence_revision <> OLD.source_evidence_revision + 1 THEN
                RAISE EXCEPTION 'orphaned model-call start settlement must advance exactly one revision (attempt=%, old=%, new=%)', NEW.id, OLD.source_evidence_revision, NEW.source_evidence_revision;
            END IF;
            IF (to_jsonb(NEW) - ARRAY['source_evidence_revision', 'status', 'error_code', 'completed_at',
                    'last_modified_date', 'last_modified_by'])
                IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['source_evidence_revision', 'status', 'error_code',
                    'completed_at', 'last_modified_date', 'last_modified_by']) THEN
                RAISE EXCEPTION 'orphaned model-call start settlement cannot rewrite projected attempt facts (attempt=%)', NEW.id;
            END IF;
        ELSIF NEW.source_evidence_revision <> OLD.source_evidence_revision THEN
            RAISE EXCEPTION 'model-call source revision may change only with one admitted evidence transition (attempt=%, old=%, new=%)', NEW.id, OLD.source_evidence_revision, NEW.source_evidence_revision;
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

COMMENT ON INDEX ix_workflow_run_model_call_attempt_late_terminal IS
    'Bounded revisit path for a durable start whose terminal fact committed later or was never captured.';
