-- 0208_model_qualification_evidence_not_null.sql
-- PostgreSQL CHECK accepts UNKNOWN. Make every v1 statistic explicitly non-null so a hand-written Bound row cannot
-- bypass the shape through SQL NULL and later crash or contaminate the empirical selector.

ALTER TABLE qualification_receipt DROP CONSTRAINT ck_qualification_receipt_model_evidence;

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
            AND model_sample_size IS NOT NULL AND model_sample_size >= 0
            AND model_observed_cell_count IS NOT NULL AND model_observed_cell_count >= 0
            AND model_observed_cell_count <= model_sample_size
            AND model_solve_rate_lower_bound IS NOT NULL AND model_solve_rate_lower_bound BETWEEN 0 AND 1
            AND model_evaluator_health IS NOT NULL AND model_evaluator_health BETWEEN 0 AND 1
            AND ((model_attribution = 'Bound' AND candidate_model_row_id IS NOT NULL AND observed_model IS NOT NULL AND btrim(observed_model) <> '' AND model_observed_cell_count > 0)
                OR (model_attribution <> 'Bound' AND observed_model IS NULL))));
