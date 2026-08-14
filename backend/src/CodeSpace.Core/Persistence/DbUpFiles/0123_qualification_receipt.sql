-- Q1 (the v4.3 qualification split): the IMMUTABLE record of a measured qualification claim — which suite
-- (by digest), judged by which verifier/model/runner bundle, over which cohort + mode-profile + capability,
-- granting which performance tier, valid for which window. Public SOTA numbers and formal capability claims must
-- trace to a Sealed receipt; the registry may REVOKE forward-only (revoked_at, a single NULL->non-NULL flip that
-- changes future gating) but a receipt's claim about the past is never rewritten — enforced by trigger: every
-- column except revoked_at is frozen at insert, DELETE is refused outright (a receipt is evidence, not state).
-- Rollback: DROP TABLE qualification_receipt CASCADE; DROP FUNCTION qualification_receipt_reject_mutations();
CREATE TABLE qualification_receipt (
    id uuid PRIMARY KEY,
    mode text NOT NULL,
    capability_key text NOT NULL,
    suite_digest text NOT NULL,
    verifier_bundle_jsonb jsonb NOT NULL,
    cohort_jsonb jsonb NOT NULL,
    granted_performance text NOT NULL,
    metrics_jsonb jsonb NULL,
    effective_from timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz NULL,
    created_date timestamptz NOT NULL,
    created_by uuid NOT NULL,
    last_modified_date timestamptz NOT NULL,
    last_modified_by uuid NOT NULL
);

CREATE INDEX idx_qualification_receipt_scope ON qualification_receipt (mode, capability_key, expires_at);

CREATE OR REPLACE FUNCTION qualification_receipt_reject_mutations() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        -- The ONE lawful transition: revoking (revoked_at NULL -> non-NULL), audit stamps riding along.
        IF NEW.id = OLD.id AND NEW.mode = OLD.mode AND NEW.capability_key = OLD.capability_key
           AND NEW.suite_digest = OLD.suite_digest AND NEW.verifier_bundle_jsonb = OLD.verifier_bundle_jsonb
           AND NEW.cohort_jsonb = OLD.cohort_jsonb AND NEW.granted_performance = OLD.granted_performance
           AND NEW.metrics_jsonb IS NOT DISTINCT FROM OLD.metrics_jsonb
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

CREATE TRIGGER qualification_receipt_enforce_immutability
    BEFORE UPDATE OR DELETE ON qualification_receipt
    FOR EACH ROW EXECUTE FUNCTION qualification_receipt_reject_mutations();
