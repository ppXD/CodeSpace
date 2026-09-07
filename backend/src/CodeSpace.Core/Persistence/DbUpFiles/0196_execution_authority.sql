-- Append-only authority does not touch workflow_run.xmin, including lazy upgrades of verified legacy runs.
CREATE TABLE workflow_run_execution_authority (
    workflow_run_id uuid PRIMARY KEY,
    team_id uuid NOT NULL,
    receipt_json jsonb NOT NULL,
    issued_at timestamptz NOT NULL,
    CONSTRAINT fk_execution_authority_run_team FOREIGN KEY (team_id, workflow_run_id) REFERENCES workflow_run(team_id, id),
    CONSTRAINT ck_execution_authority_identity CHECK (
        receipt_json ? 'logicalRunId' AND receipt_json ? 'teamId'
        AND jsonb_typeof(receipt_json->'logicalRunId') = 'string' AND jsonb_typeof(receipt_json->'teamId') = 'string'
        AND (receipt_json->>'logicalRunId')::uuid = workflow_run_id
        AND (receipt_json->>'teamId')::uuid = team_id
    )
);
CREATE FUNCTION protect_workflow_run_execution_authority() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'workflow execution authority is immutable';
END;
$$;
CREATE TRIGGER workflow_run_execution_authority_immutable BEFORE UPDATE OR DELETE ON workflow_run_execution_authority
FOR EACH ROW EXECUTE FUNCTION protect_workflow_run_execution_authority();

ALTER TABLE workflow_activation ADD COLUMN authority_revision uuid NOT NULL DEFAULT gen_random_uuid();
CREATE FUNCTION rotate_workflow_activation_authority_revision() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    NEW.authority_revision := gen_random_uuid();
    RETURN NEW;
END;
$$;
CREATE TRIGGER workflow_activation_authority_revision BEFORE UPDATE ON workflow_activation
FOR EACH ROW EXECUTE FUNCTION rotate_workflow_activation_authority_revision();
