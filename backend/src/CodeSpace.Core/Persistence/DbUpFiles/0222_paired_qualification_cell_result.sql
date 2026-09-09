-- Seal the terminal benchmark result into its admission before fixture cleanup and observation append. Recovery can
-- copy this canonical result into benchmark_result without invoking the model again.
ALTER TABLE paired_qualification_cell_admission ADD COLUMN result_json jsonb NULL;
ALTER TABLE paired_qualification_cell_admission ADD COLUMN completed_at timestamptz NULL;
ALTER TABLE paired_qualification_cell_admission ADD CONSTRAINT ck_paired_qualification_cell_result CHECK (
    (result_json IS NULL AND completed_at IS NULL)
    OR (jsonb_typeof(result_json) = 'object' AND completed_at IS NOT NULL
        AND result_json ->> 'taskId' = task_id
        AND result_json ->> 'mode' = mode));

DROP TRIGGER paired_qualification_cell_admission_immutable ON paired_qualification_cell_admission;
CREATE OR REPLACE FUNCTION paired_qualification_cell_admission_reject_mutations() RETURNS trigger AS $$
BEGIN
    IF OLD.result_json IS NULL AND NEW.result_json IS NOT NULL
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

CREATE TRIGGER paired_qualification_cell_admission_immutable
    BEFORE UPDATE OR DELETE ON paired_qualification_cell_admission
    FOR EACH ROW EXECUTE FUNCTION paired_qualification_cell_admission_reject_mutations();

CREATE OR REPLACE FUNCTION benchmark_result_require_cell_admission() RETURNS trigger AS $$
DECLARE
    admission_required boolean;
    terminal_result jsonb;
BEGIN
    IF NEW.observation_group_id IS NULL THEN RETURN NEW; END IF;
    SELECT requires_cell_admission INTO admission_required
      FROM paired_qualification_protocol
     WHERE observation_group_id = NEW.observation_group_id;
    IF admission_required THEN
        SELECT result_json INTO terminal_result
          FROM paired_qualification_cell_admission
         WHERE observation_group_id = NEW.observation_group_id
           AND observation_session = NEW.observation_session
           AND observation_arm = NEW.observation_arm
           AND task_id = NEW.task_id
           AND mode = NEW.mode
           AND model_credential_model_id = NEW.model_credential_model_id;
        IF NOT FOUND THEN RAISE EXCEPTION 'paired benchmark observation has no matching durable cell admission'; END IF;
        IF terminal_result IS NULL AND NEW.outcome_state <> 'InfraUnknown' THEN
            RAISE EXCEPTION 'paired benchmark graded observation has no sealed admission result';
        END IF;
        IF terminal_result IS NOT NULL AND NEW.outcome_state = 'InfraUnknown' THEN
            RAISE EXCEPTION 'paired benchmark terminal result cannot be replaced by an infrastructure observation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON COLUMN paired_qualification_cell_admission.result_json IS 'Canonical terminal BenchmarkResult sealed before fixture cleanup and observation append; null means execution remains indeterminate or ended before a terminal result existed.';
