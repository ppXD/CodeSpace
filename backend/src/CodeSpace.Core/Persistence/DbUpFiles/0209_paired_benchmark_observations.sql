-- 0209_paired_benchmark_observations.sql
-- Durable cell provenance for paired TaskLaunch qualification. Existing rows remain valid observations;
-- their state is derived from the already-stored objective grade. New infra rows use the same append-only table.

ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS model_credential_model_id UUID NULL;
ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS observed_model VARCHAR(200) NULL;
ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS observation_group_id UUID NULL;
ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS observation_arm VARCHAR(40) NULL;
ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS observation_session INTEGER NULL;
ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS outcome_state VARCHAR(30) NULL;
ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS outcome_detail TEXT NULL;
ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS cost_indeterminate BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE benchmark_result ADD COLUMN IF NOT EXISTS max_cost_usd NUMERIC(18,6) NULL;

UPDATE benchmark_result SET outcome_state = CASE WHEN solved THEN 'Solved' ELSE 'Unsolved' END WHERE outcome_state IS NULL;
ALTER TABLE benchmark_result ALTER COLUMN outcome_state SET NOT NULL;

ALTER TABLE benchmark_result ADD CONSTRAINT ck_benchmark_result_outcome_state CHECK (outcome_state IN ('Solved', 'Unsolved', 'Abstained', 'InfraUnknown'));
ALTER TABLE benchmark_result ADD CONSTRAINT ck_benchmark_result_indeterminate_cost CHECK (
    NOT cost_indeterminate OR cost_usd IS NULL);

ALTER TABLE benchmark_result DROP CONSTRAINT IF EXISTS ck_benchmark_result_observation_group;
ALTER TABLE benchmark_result ADD CONSTRAINT ck_benchmark_result_observation_group CHECK (
    (observation_group_id IS NULL AND observation_arm IS NULL AND observation_session IS NULL)
    OR (observation_group_id IS NOT NULL AND observation_arm IN ('control', 'candidate') AND observation_session >= 0
        AND model_credential_model_id IS NOT NULL AND max_cost_usd > 0
        AND (cost_usd IS NOT NULL OR cost_indeterminate)
        AND (outcome_state = 'InfraUnknown' OR NULLIF(BTRIM(observed_model), '') IS NOT NULL)));
ALTER TABLE benchmark_result DROP CONSTRAINT IF EXISTS ck_benchmark_result_outcome_truth;
ALTER TABLE benchmark_result ADD CONSTRAINT ck_benchmark_result_outcome_truth CHECK (
    solved = (outcome_state = 'Solved'));
ALTER TABLE benchmark_result DROP CONSTRAINT IF EXISTS ck_benchmark_result_cost;
ALTER TABLE benchmark_result ADD CONSTRAINT ck_benchmark_result_cost CHECK (
    cost_usd IS NULL OR (cost_usd >= 0 AND cost_indeterminate = FALSE));
ALTER TABLE benchmark_result DROP CONSTRAINT IF EXISTS ck_benchmark_result_cost_cap;
ALTER TABLE benchmark_result ADD CONSTRAINT ck_benchmark_result_cost_cap CHECK (max_cost_usd IS NULL OR max_cost_usd > 0);

DROP INDEX IF EXISTS ix_benchmark_result_observation_group;
CREATE UNIQUE INDEX ix_benchmark_result_observation_group
    ON benchmark_result (team_id, observation_group_id, observation_arm, observation_session, task_id, mode)
    WHERE observation_group_id IS NOT NULL;

COMMENT ON COLUMN benchmark_result.observation_group_id IS 'Immutable identity shared by both arms and every session of one paired qualification campaign.';
COMMENT ON COLUMN benchmark_result.outcome_state IS 'Fixed-denominator state; InfraUnknown rows preserve evaluator failures instead of disappearing.';

CREATE OR REPLACE FUNCTION benchmark_result_reject_mutations() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'benchmark_result is append-only — % rejected (id=%). Qualification history is never rewritten.', TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS benchmark_result_enforce_immutability ON benchmark_result;
CREATE TRIGGER benchmark_result_enforce_immutability BEFORE UPDATE OR DELETE ON benchmark_result
    FOR EACH ROW EXECUTE FUNCTION benchmark_result_reject_mutations();
