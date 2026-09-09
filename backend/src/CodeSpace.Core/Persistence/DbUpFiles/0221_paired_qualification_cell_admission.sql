-- One immutable authorization before each paid paired cell. If an admission exists without its matching
-- benchmark_result, execution crossed an uncertain boundary and automatic replay would risk duplicate billing.
ALTER TABLE paired_qualification_protocol ADD COLUMN requires_cell_admission boolean NOT NULL DEFAULT FALSE;

CREATE TABLE paired_qualification_cell_admission (
    id uuid PRIMARY KEY,
    observation_group_id uuid NOT NULL REFERENCES paired_qualification_protocol(observation_group_id) ON DELETE RESTRICT,
    observation_session integer NOT NULL,
    observation_arm varchar(20) NOT NULL,
    task_id varchar(200) NOT NULL,
    mode varchar(40) NOT NULL,
    model_credential_model_id uuid NOT NULL,
    created_date timestamptz NOT NULL,
    created_by uuid NOT NULL,
    last_modified_date timestamptz NOT NULL,
    last_modified_by uuid NOT NULL,
    CONSTRAINT ck_paired_qualification_cell_admission_shape CHECK (
        id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND observation_group_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND observation_session >= 0
        AND observation_arm IN ('control', 'candidate')
        AND btrim(task_id) <> '' AND btrim(mode) <> ''
        AND model_credential_model_id <> '00000000-0000-0000-0000-000000000000'::uuid)
);

ALTER TABLE paired_qualification_cell_admission ADD CONSTRAINT uq_paired_qualification_cell_admission_key
    UNIQUE (observation_group_id, observation_session, observation_arm, task_id, mode);

CREATE OR REPLACE FUNCTION paired_qualification_cell_admission_validate() RETURNS trigger AS $$
DECLARE
    protocol_sessions integer;
    expected_model_row_id uuid;
BEGIN
    SELECT sessions_per_cell,
           CASE NEW.observation_arm WHEN 'control' THEN control_model_row_id ELSE candidate_model_row_id END
      INTO protocol_sessions, expected_model_row_id
      FROM paired_qualification_protocol
     WHERE observation_group_id = NEW.observation_group_id;

    IF NEW.observation_session >= protocol_sessions THEN
        RAISE EXCEPTION 'paired qualification cell session is outside the immutable protocol';
    END IF;
    IF NEW.model_credential_model_id <> expected_model_row_id THEN
        RAISE EXCEPTION 'paired qualification cell model row does not match its immutable protocol arm';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER paired_qualification_cell_admission_validate_insert
    BEFORE INSERT ON paired_qualification_cell_admission
    FOR EACH ROW EXECUTE FUNCTION paired_qualification_cell_admission_validate();

CREATE OR REPLACE FUNCTION paired_qualification_cell_admission_reject_mutations() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'paired qualification cell admission is immutable — % rejected (id=%)', TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER paired_qualification_cell_admission_immutable
    BEFORE UPDATE OR DELETE ON paired_qualification_cell_admission
    FOR EACH ROW EXECUTE FUNCTION paired_qualification_cell_admission_reject_mutations();

CREATE OR REPLACE FUNCTION benchmark_result_require_cell_admission() RETURNS trigger AS $$
DECLARE
    admission_required boolean;
BEGIN
    IF NEW.observation_group_id IS NULL THEN RETURN NEW; END IF;
    SELECT requires_cell_admission INTO admission_required
      FROM paired_qualification_protocol
     WHERE observation_group_id = NEW.observation_group_id;
    IF admission_required AND NOT EXISTS (
        SELECT 1 FROM paired_qualification_cell_admission
         WHERE observation_group_id = NEW.observation_group_id
           AND observation_session = NEW.observation_session
           AND observation_arm = NEW.observation_arm
           AND task_id = NEW.task_id
           AND mode = NEW.mode
           AND model_credential_model_id = NEW.model_credential_model_id) THEN
        RAISE EXCEPTION 'paired benchmark observation has no matching durable cell admission';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER benchmark_result_require_cell_admission_insert
    BEFORE INSERT ON benchmark_result
    FOR EACH ROW EXECUTE FUNCTION benchmark_result_require_cell_admission();

COMMENT ON TABLE paired_qualification_cell_admission IS 'Immutable pre-execution authorization. A row without a matching observation is indeterminate and cannot be replayed automatically.';
COMMENT ON COLUMN paired_qualification_protocol.requires_cell_admission IS 'True for campaigns created after admission enforcement; false preserves recovery of legacy protocols.';
