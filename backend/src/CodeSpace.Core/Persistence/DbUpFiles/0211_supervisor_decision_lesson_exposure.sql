-- Exact per-turn lesson exposure is durable evaluation evidence. Empty means no lesson text reached the prompt.
-- Existing rows cannot be reconstructed honestly, so they retain the empty default.
ALTER TABLE supervisor_decision ADD COLUMN IF NOT EXISTS lesson_ids UUID[] NOT NULL DEFAULT '{}';

COMMENT ON COLUMN supervisor_decision.lesson_ids IS
    'Exact lesson ids exposed to the model prompt for this decision. Empty means no lesson text was injected. '
    'Frozen at insert; historical exposure must never be inferred from the mutable lesson ledger.';

CREATE OR REPLACE FUNCTION supervisor_decision_reject_journal_mutations() RETURNS TRIGGER AS $$
BEGIN
    IF (TG_OP = 'DELETE') THEN
        RAISE EXCEPTION
            'supervisor_decision is permanent audit — DELETE rejected (run=%, sequence=%, kind=%).',
            OLD.supervisor_run_id, OLD.sequence, OLD.decision_kind;
    END IF;

    IF (NEW.payload_jsonb IS DISTINCT FROM OLD.payload_jsonb
        OR NEW.sequence IS DISTINCT FROM OLD.sequence
        OR NEW.decision_kind IS DISTINCT FROM OLD.decision_kind
        OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
        OR NEW.team_id IS DISTINCT FROM OLD.team_id
        OR NEW.supervisor_run_id IS DISTINCT FROM OLD.supervisor_run_id
        OR NEW.fence_epoch IS DISTINCT FROM OLD.fence_epoch
        OR NEW.lesson_arm IS DISTINCT FROM OLD.lesson_arm
        OR NEW.lesson_ids IS DISTINCT FROM OLD.lesson_ids) THEN
        RAISE EXCEPTION
            'supervisor_decision journal + identity fields are frozen at insert — UPDATE rejected (run=%, sequence=%, kind=%). '
            'Only the status path (status/outcome_jsonb/error) is mutable.',
            OLD.supervisor_run_id, OLD.sequence, OLD.decision_kind;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
