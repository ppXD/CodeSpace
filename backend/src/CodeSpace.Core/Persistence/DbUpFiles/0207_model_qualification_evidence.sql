-- 0207_model_qualification_evidence.sql
-- Queryable, immutable model attribution and uncertainty on the existing qualification receipt.
-- The selected row is deliberately a soft historical reference: pool maintenance must never cascade-delete evidence.

ALTER TABLE qualification_receipt
    ADD COLUMN model_evidence_version varchar(80) NULL,
    ADD COLUMN candidate_model_row_id uuid NULL,
    ADD COLUMN observed_model varchar(300) NULL,
    ADD COLUMN model_attribution varchar(32) NOT NULL DEFAULT 'LegacyUnknown',
    ADD COLUMN model_sample_size integer NULL,
    ADD COLUMN model_observed_cell_count integer NULL,
    ADD COLUMN model_solve_rate_lower_bound double precision NULL,
    ADD COLUMN model_evaluator_health double precision NULL;

ALTER TABLE qualification_receipt
    ADD CONSTRAINT ck_qualification_receipt_model_evidence CHECK (
        (model_evidence_version IS NULL
            AND model_attribution = 'LegacyUnknown'
            AND candidate_model_row_id IS NULL
            AND observed_model IS NULL
            AND model_sample_size IS NULL
            AND model_observed_cell_count IS NULL
            AND model_solve_rate_lower_bound IS NULL
            AND model_evaluator_health IS NULL)
        OR
        (model_evidence_version IS NOT NULL
            AND model_attribution IN ('Bound', 'UnboundSelection', 'MissingObservation', 'InconsistentObservation', 'NonLaunchPath')
            AND model_sample_size >= 0
            AND model_observed_cell_count >= 0
            AND model_observed_cell_count <= model_sample_size
            AND model_solve_rate_lower_bound BETWEEN 0 AND 1
            AND model_evaluator_health BETWEEN 0 AND 1
            AND ((model_attribution = 'Bound' AND candidate_model_row_id IS NOT NULL AND observed_model IS NOT NULL AND btrim(observed_model) <> '')
                OR (model_attribution <> 'Bound' AND observed_model IS NULL))));

CREATE INDEX ix_qualification_receipt_model_evidence
    ON qualification_receipt (candidate_model_row_id, capability_key, expires_at)
    WHERE model_attribution = 'Bound' AND revoked_at IS NULL;

CREATE OR REPLACE FUNCTION qualification_receipt_reject_mutations() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        -- The ONE lawful transition: revoking (revoked_at NULL -> non-NULL), audit stamps riding along.
        IF NEW.id = OLD.id AND NEW.mode = OLD.mode AND NEW.capability_key = OLD.capability_key
           AND NEW.suite_digest = OLD.suite_digest AND NEW.verifier_bundle_jsonb = OLD.verifier_bundle_jsonb
           AND NEW.cohort_jsonb = OLD.cohort_jsonb AND NEW.granted_performance = OLD.granted_performance
           AND NEW.metrics_jsonb IS NOT DISTINCT FROM OLD.metrics_jsonb
           AND NEW.model_evidence_version IS NOT DISTINCT FROM OLD.model_evidence_version
           AND NEW.candidate_model_row_id IS NOT DISTINCT FROM OLD.candidate_model_row_id
           AND NEW.observed_model IS NOT DISTINCT FROM OLD.observed_model
           AND NEW.model_attribution = OLD.model_attribution
           AND NEW.model_sample_size IS NOT DISTINCT FROM OLD.model_sample_size
           AND NEW.model_observed_cell_count IS NOT DISTINCT FROM OLD.model_observed_cell_count
           AND NEW.model_solve_rate_lower_bound IS NOT DISTINCT FROM OLD.model_solve_rate_lower_bound
           AND NEW.model_evaluator_health IS NOT DISTINCT FROM OLD.model_evaluator_health
           AND NEW.effective_from = OLD.effective_from AND NEW.expires_at = OLD.expires_at
           AND NEW.created_date = OLD.created_date AND NEW.created_by = OLD.created_by
           AND OLD.revoked_at IS NULL AND NEW.revoked_at IS NOT NULL THEN
            RETURN NEW;
        END IF;
    END IF;

    RAISE EXCEPTION
        'qualification_receipt is immutable — % rejected (id=%). The only lawful mutation is the one-way revoke (revoked_at NULL -> set); a claim about the past is never rewritten.',
        TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON COLUMN qualification_receipt.candidate_model_row_id IS
    'Soft historical reference to the selected credentialed-model row; never a cascading FK.';
COMMENT ON COLUMN qualification_receipt.observed_model IS
    'Single provider-reported backing identity across every capability-verdict cell; null unless attribution is Bound.';
COMMENT ON COLUMN qualification_receipt.model_solve_rate_lower_bound IS
    'One-sided 95% Wilson lower bound over the frozen suite denominator.';
