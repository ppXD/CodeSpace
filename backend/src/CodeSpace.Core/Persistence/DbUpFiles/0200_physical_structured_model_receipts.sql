-- Physical structured POST receipts share the existing model-call plane and budget lock.
-- Numeric matches 0198: preserve every representable .NET decimal; estimates are not invoice caps.
ALTER TABLE budget_reservation ADD CONSTRAINT ak_budget_reservation_scope UNIQUE (id, team_id, workflow_run_id);
ALTER TABLE workflow_run_model_call_attempt
    ALTER COLUMN cost_amount TYPE NUMERIC,
    ADD COLUMN candidate_id UUID NULL,
    ADD COLUMN candidate_ordinal INTEGER NULL,
    ADD COLUMN candidate_model VARCHAR(500) NULL,
    ADD COLUMN budget_reservation_id UUID NULL,
    ADD COLUMN pricing_snapshot_json JSONB NULL,
    ADD COLUMN usage_is_partial BOOLEAN NOT NULL DEFAULT FALSE,
    ADD CONSTRAINT fk_model_call_attempt_budget_scope FOREIGN KEY (budget_reservation_id, team_id, workflow_run_id)
        REFERENCES budget_reservation (id, team_id, workflow_run_id) ON DELETE RESTRICT,
    ADD CONSTRAINT ck_model_call_attempt_physical_receipt CHECK (
        (candidate_id IS NULL AND candidate_ordinal IS NULL AND candidate_model IS NULL AND budget_reservation_id IS NULL AND pricing_snapshot_json IS NULL)
        OR (candidate_id IS NOT NULL AND candidate_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND candidate_ordinal IS NOT NULL AND candidate_ordinal > 0 AND candidate_model IS NOT NULL AND btrim(candidate_model) <> '' AND budget_reservation_id IS NOT NULL
            AND pricing_snapshot_json IS NOT NULL AND jsonb_typeof(pricing_snapshot_json) = 'object'
            AND octet_length(pricing_snapshot_json::text) <= 524288 AND capture_source = 'structured-post/v1'));
CREATE UNIQUE INDEX ux_model_call_attempt_budget_reservation ON workflow_run_model_call_attempt (budget_reservation_id) WHERE budget_reservation_id IS NOT NULL;

CREATE FUNCTION model_call_attempt_physical_identity_guard() RETURNS TRIGGER AS $$
BEGIN
    IF OLD.budget_reservation_id IS NOT NULL AND
        (NEW.id, NEW.team_id, NEW.workflow_run_id, NEW.model_call_id, NEW.attempt_ordinal, NEW.candidate_id,
         NEW.candidate_ordinal, NEW.candidate_model, NEW.effective_provider, NEW.transport_kind, NEW.budget_reservation_id, NEW.pricing_snapshot_json, NEW.pricing_version)
        IS DISTINCT FROM
        (OLD.id, OLD.team_id, OLD.workflow_run_id, OLD.model_call_id, OLD.attempt_ordinal, OLD.candidate_id,
         OLD.candidate_ordinal, OLD.candidate_model, OLD.effective_provider, OLD.transport_kind, OLD.budget_reservation_id, OLD.pricing_snapshot_json, OLD.pricing_version) THEN
        RAISE EXCEPTION 'physical model invocation identity and frozen pricing are immutable';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER model_call_attempt_physical_identity_enforce BEFORE UPDATE ON workflow_run_model_call_attempt
    FOR EACH ROW EXECUTE FUNCTION model_call_attempt_physical_identity_guard();
