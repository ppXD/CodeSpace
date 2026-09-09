-- Freeze every treatment-bearing selection field before paid work. Existing protocols remain readable for result
-- recovery but cannot partially resume because their original harness/autonomy/review treatment is unknowable.
ALTER TABLE paired_qualification_protocol ADD COLUMN control_selection_json jsonb NULL;
ALTER TABLE paired_qualification_protocol ADD COLUMN candidate_selection_json jsonb NULL;

ALTER TABLE paired_qualification_protocol ADD CONSTRAINT ck_paired_qualification_selection_snapshot CHECK (
    (control_selection_json IS NULL AND candidate_selection_json IS NULL)
    OR (
        jsonb_typeof(control_selection_json) = 'object'
        AND jsonb_typeof(candidate_selection_json) = 'object'
        AND control_selection_json ? 'modelCredentialModelId'
        AND candidate_selection_json ? 'modelCredentialModelId'
        AND control_selection_json ? 'maxCostUsd'
        AND candidate_selection_json ? 'maxCostUsd'
        AND (control_selection_json ->> 'modelCredentialModelId')::uuid = control_model_row_id
        AND (candidate_selection_json ->> 'modelCredentialModelId')::uuid = candidate_model_row_id
        AND (control_selection_json ->> 'maxCostUsd')::numeric = max_cost_usd_per_launch
        AND (candidate_selection_json ->> 'maxCostUsd')::numeric = max_cost_usd_per_launch));

COMMENT ON COLUMN paired_qualification_protocol.control_selection_json IS 'Canonical full control treatment snapshot; null only for protocols created before migration 0220.';
COMMENT ON COLUMN paired_qualification_protocol.candidate_selection_json IS 'Canonical full candidate treatment snapshot; null only for protocols created before migration 0220.';
