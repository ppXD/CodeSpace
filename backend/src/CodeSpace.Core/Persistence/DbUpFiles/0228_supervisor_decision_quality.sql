-- 0228_supervisor_decision_quality.sql
--
-- P22-9b — record WHAT THE QUALITY POLICY RECOMMENDED on the decision the turn actually emitted.
--
-- The policy (P22-9a) is pure and, until 9b, had no caller: it recommended nothing to nobody. 9b recites its
-- per-unit reading into the decider prompt, where the model is explicitly free to reject it. That freedom is the
-- point — and it is also why the recommendation has to be durable: "the policy said escalate the model and the
-- brain retried on the same one anyway" is not recoverable after the fact from a prompt nobody kept. 9c's
-- same-budget ablation is precisely a comparison between what the policy recommended and what the run did, so
-- without this column that comparison has no left-hand side.
--
-- `quality_decisions` holds the array of per-unit readings (subtask id + mechanism + the reason citing the facts
-- that chose it + the facts themselves), exactly as the prompt carried them. NULL means "no unit had been attempted
-- when this decision was emitted, or the row predates this column" — the two are not distinguished, and neither
-- needs to be: both mean there is nothing recorded to compare a decision against.
--
-- It is a JOURNAL field, not a CAS field, and the frozen-column list in
-- supervisor_decision_reject_journal_mutations() is extended to cover it — for the same reason 0167 extended it to
-- lesson_arm: evidence that can be rewritten after the decision it describes is not evidence. The function is
-- replaced wholesale (0211's body plus the one new column) because CREATE OR REPLACE FUNCTION takes the whole body;
-- IS DISTINCT FROM keeps the NULL-to-NULL no-op every status CAS performs allowed.
--
-- Additive + non-breaking: a nullable column plus a widened freeze. Idempotent (IF NOT EXISTS / OR REPLACE).

ALTER TABLE supervisor_decision ADD COLUMN IF NOT EXISTS quality_decisions jsonb NULL;

COMMENT ON COLUMN supervisor_decision.quality_decisions IS
    'P22 quality-policy recommendations the turn that emitted this decision was shown, one entry per ATTEMPTED plan '
    'unit: subtask id, mechanism, the reason citing the facts that chose it, and those facts. A RECOMMENDATION the '
    'model was free to reject — nothing branches on it — kept so a later reader (and 9c''s ablation) can compare what '
    'was recommended with what the run did. NULL = no unit had been attempted yet, or the row predates the column. '
    'Frozen at insert by supervisor_decision_enforce_immutability.';

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
        OR NEW.lesson_ids IS DISTINCT FROM OLD.lesson_ids
        OR NEW.quality_decisions IS DISTINCT FROM OLD.quality_decisions) THEN
        RAISE EXCEPTION
            'supervisor_decision journal + identity fields are frozen at insert — UPDATE rejected (run=%, sequence=%, kind=%). '
            'Only the status path (status/outcome_jsonb/error) is mutable.',
            OLD.supervisor_run_id, OLD.sequence, OLD.decision_kind;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
