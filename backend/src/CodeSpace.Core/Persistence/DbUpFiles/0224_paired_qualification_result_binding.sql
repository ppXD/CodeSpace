-- Bind each new paired observation to the exact terminal BenchmarkResult sealed by its cell admission.
-- Legacy protocols remain readable and recoverable through the explicit false default.
ALTER TABLE paired_qualification_protocol ADD COLUMN requires_result_digest boolean NOT NULL DEFAULT FALSE;
ALTER TABLE paired_qualification_cell_admission ADD COLUMN result_digest varchar(64) NULL;
ALTER TABLE paired_qualification_cell_admission ADD COLUMN result_projection_json jsonb NULL;
ALTER TABLE benchmark_result ADD COLUMN source_result_digest varchar(64) NULL;

ALTER TABLE paired_qualification_cell_admission ADD CONSTRAINT ck_paired_qualification_cell_result_digest
    CHECK (result_digest IS NULL OR result_digest ~ '^[0-9A-F]{64}$');
ALTER TABLE benchmark_result ADD CONSTRAINT ck_benchmark_result_source_result_digest
    CHECK (source_result_digest IS NULL OR source_result_digest ~ '^[0-9A-F]{64}$');
ALTER TABLE paired_qualification_cell_admission ADD CONSTRAINT ck_paired_qualification_cell_result_projection
    CHECK (result_projection_json IS NULL OR jsonb_typeof(result_projection_json) = 'object');

CREATE OR REPLACE FUNCTION paired_qualification_cell_admission_reject_precompleted_insert() RETURNS trigger AS $$
BEGIN
    IF NEW.result_json IS NOT NULL OR NEW.result_digest IS NOT NULL OR NEW.result_projection_json IS NOT NULL OR NEW.completed_at IS NOT NULL THEN
        RAISE EXCEPTION 'paired qualification cell admission must begin open';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER paired_qualification_cell_admission_open_insert
    BEFORE INSERT ON paired_qualification_cell_admission
    FOR EACH ROW EXECUTE FUNCTION paired_qualification_cell_admission_reject_precompleted_insert();

CREATE OR REPLACE FUNCTION paired_qualification_cell_admission_reject_mutations() RETURNS trigger AS $$
BEGIN
    IF OLD.result_json IS NULL AND OLD.result_digest IS NULL AND OLD.result_projection_json IS NULL
       AND NEW.result_json IS NOT NULL AND NEW.result_digest ~ '^[0-9A-F]{64}$'
       AND jsonb_typeof(NEW.result_projection_json) = 'object'
       AND OLD.id = NEW.id
       AND OLD.observation_group_id = NEW.observation_group_id
       AND OLD.observation_session = NEW.observation_session
       AND OLD.observation_arm = NEW.observation_arm
       AND OLD.task_id = NEW.task_id
       AND OLD.mode = NEW.mode
       AND OLD.model_credential_model_id = NEW.model_credential_model_id
       AND OLD.created_date = NEW.created_date
       AND OLD.created_by = NEW.created_by
       AND NEW.completed_at IS NOT NULL THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'paired qualification cell admission is immutable — % rejected (id=%)', TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION benchmark_result_require_cell_admission() RETURNS trigger AS $$
DECLARE
    admission_required boolean;
    digest_required boolean;
    terminal_result jsonb;
    terminal_digest varchar(64);
    terminal_projection jsonb;
BEGIN
    IF NEW.observation_group_id IS NULL THEN RETURN NEW; END IF;
    SELECT requires_cell_admission, requires_result_digest INTO admission_required, digest_required
      FROM paired_qualification_protocol
     WHERE observation_group_id = NEW.observation_group_id;
    IF admission_required THEN
        SELECT result_json, result_digest, result_projection_json INTO terminal_result, terminal_digest, terminal_projection
          FROM paired_qualification_cell_admission
         WHERE observation_group_id = NEW.observation_group_id
           AND observation_session = NEW.observation_session
           AND observation_arm = NEW.observation_arm
           AND task_id = NEW.task_id
           AND mode = NEW.mode
           AND model_credential_model_id = NEW.model_credential_model_id;
        IF NOT FOUND THEN RAISE EXCEPTION 'paired benchmark observation has no matching durable cell admission'; END IF;
        IF terminal_result IS NULL AND (NEW.outcome_state <> 'InfraUnknown' OR NEW.source_result_digest IS NOT NULL) THEN
            RAISE EXCEPTION 'paired benchmark graded observation has no sealed admission result';
        END IF;
        IF terminal_result IS NOT NULL AND NEW.outcome_state = 'InfraUnknown' AND (NOT digest_required OR NEW.source_result_digest IS NULL) THEN
            RAISE EXCEPTION 'paired benchmark terminal result cannot be replaced by an infrastructure observation';
        END IF;
        IF digest_required AND terminal_result IS NOT NULL AND (
            terminal_digest IS NULL OR terminal_projection IS NULL OR NEW.source_result_digest IS DISTINCT FROM terminal_digest
            OR NEW.agent_run_id IS DISTINCT FROM NULLIF(terminal_projection ->> 'agentRunId', '')::uuid
            OR NEW.observed_model IS DISTINCT FROM terminal_projection ->> 'observedModel'
            OR NEW.outcome_state IS DISTINCT FROM terminal_projection ->> 'outcomeState'
            OR NEW.outcome_detail IS DISTINCT FROM terminal_projection ->> 'outcomeDetail'
            OR NEW.solved IS DISTINCT FROM (terminal_projection ->> 'solved')::boolean
            OR NEW.run_status IS DISTINCT FROM terminal_projection ->> 'runStatus'
            OR NEW.revise_rounds IS DISTINCT FROM (terminal_projection ->> 'reviseRounds')::integer
            OR NEW.mcp_full_catalog IS DISTINCT FROM (terminal_projection ->> 'mcpFullCatalog')::boolean
            OR NEW.exit_reason IS DISTINCT FROM terminal_projection ->> 'exitReason'
            OR NEW.cost_usd IS DISTINCT FROM (terminal_projection ->> 'costUsd')::numeric
            OR NEW.cost_indeterminate IS DISTINCT FROM (terminal_projection ->> 'costIndeterminate')::boolean
            OR NEW.duration_seconds IS DISTINCT FROM (terminal_projection ->> 'durationSeconds')::double precision) THEN
            RAISE EXCEPTION 'paired benchmark observation does not match its sealed admission result';
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON COLUMN paired_qualification_protocol.requires_result_digest IS 'True when every graded observation must prove projection from the exact terminal cell result; false preserves legacy protocols.';
COMMENT ON COLUMN paired_qualification_cell_admission.result_digest IS 'SHA-256 of the exact canonical BenchmarkResult JSON single-assigned with result_json.';
COMMENT ON COLUMN paired_qualification_cell_admission.result_projection_json IS 'Canonical result-derived benchmark_result columns, single-assigned with the terminal result so PostgreSQL can reject projection drift without reimplementing application classification policy.';
COMMENT ON COLUMN benchmark_result.source_result_digest IS 'Digest copied from the paired cell terminal result and verified with its projected fields at insert.';
