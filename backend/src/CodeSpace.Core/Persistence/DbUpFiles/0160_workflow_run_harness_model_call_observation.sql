-- 0160_workflow_run_harness_model_call_observation.sql
--
-- Snapshot the harness adapter's MODEL-CALL OBSERVATION GRANULARITY on the durable physical execution. This is a
-- declaration about the stable native protocol that actually ran, not a claim that capture succeeded and not model
-- request/response body completeness. It therefore belongs to the execution identity beside harness_type_key.
--
-- Existing rows remain NULL deliberately: deriving Claude/Codex coverage from harness_type_key would reinterpret an
-- old run through today's adapter. New writers state PerResponseMetadata, CumulativeAggregate, Unavailable, or
-- LegacyUnknown explicitly. The column remains an open canonical token so a future writer can add a value without a
-- database rewrite; older backend/UI readers must treat an unknown token as LegacyUnknown rather than materializing
-- it through an enum or displaying a stronger known claim.
--
-- Rollback: DROP TRIGGER workflow_run_harness_execution_model_call_observation_immutable ON workflow_run_harness_execution;
--           DROP FUNCTION workflow_run_harness_execution_model_call_observation_immutable();
--           ALTER TABLE workflow_run_harness_execution DROP CONSTRAINT ck_workflow_run_harness_execution_model_call_observation;
--           ALTER TABLE workflow_run_harness_execution DROP COLUMN model_call_observation_coverage;

ALTER TABLE workflow_run_harness_execution
    ADD COLUMN model_call_observation_coverage VARCHAR(48) NULL,
    ADD CONSTRAINT ck_workflow_run_harness_execution_model_call_observation CHECK (
        model_call_observation_coverage IS NULL
        OR model_call_observation_coverage ~ '^[A-Z][A-Za-z0-9]{0,47}$');

CREATE OR REPLACE FUNCTION workflow_run_harness_execution_model_call_observation_immutable() RETURNS trigger AS $$
BEGIN
    IF NEW.model_call_observation_coverage IS DISTINCT FROM OLD.model_call_observation_coverage THEN
        RAISE EXCEPTION 'workflow_run_harness_execution model-call observation coverage is immutable (id=%).', OLD.id;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_harness_execution_model_call_observation_immutable
    BEFORE UPDATE ON workflow_run_harness_execution
    FOR EACH ROW EXECUTE FUNCTION workflow_run_harness_execution_model_call_observation_immutable();

COMMENT ON COLUMN workflow_run_harness_execution.model_call_observation_coverage IS
    'Adapter-declared physical model-call observation granularity at launch. NULL is legacy unknown; an unknown future canonical token must fail closed at the reader.';
