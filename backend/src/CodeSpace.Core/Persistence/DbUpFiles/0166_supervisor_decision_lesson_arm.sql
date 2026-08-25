-- 0166_supervisor_decision_lesson_arm.sql
--
-- D2 (cross-run learning) — record the lesson-experiment ARM on the supervisor lane.
--
-- The supervisor's turn prompt has carried distilled lessons since the D2 wiring landed, but nothing recorded WHICH
-- arm a run was in: the only writer of an arm anywhere was the workflow planner, onto its authored plan. A treatment
-- with no recorded assignment is worse than no treatment at all — the untreated runs cannot be told apart from the
-- treated ones, so they silently contaminate the control group and no scorecard can slice the two.
--
-- `lesson_arm` holds a LessonArms value ('injected' | 'withheld' | 'none'); NULL means "outside the experiment /
-- written before this column existed", which is what every pre-existing row keeps. It is stamped on every decision
-- row a run writes, and the run's rehydrate reads the earliest non-null value back and reuses it — that read is what
-- pins a run's arm across turns, so a run cannot be promoted into the treatment by a lesson distilled mid-run.
--
-- It is a JOURNAL field, not a CAS field: an assignment that could be rewritten after the fact is not evidence. The
-- frozen-column list in supervisor_decision_reject_journal_mutations() is therefore extended to cover it (IS DISTINCT
-- FROM, so the NULL-to-NULL no-op every legacy row's status CAS performs stays allowed). The function is replaced
-- wholesale — 0053's body plus the one new column — because CREATE OR REPLACE FUNCTION takes the whole body.
--
-- Additive + non-breaking: a nullable column plus a widened freeze. Idempotent (IF NOT EXISTS / OR REPLACE).

ALTER TABLE supervisor_decision ADD COLUMN IF NOT EXISTS lesson_arm VARCHAR(16) NULL;

COMMENT ON COLUMN supervisor_decision.lesson_arm IS
    'D2 lesson A/B arm this decision''s prompt was built under: injected | withheld | none (a LessonArms value). '
    'NULL = outside the experiment, or written before the column existed. Assigned at the run''s first decision from '
    '(team_id, the run''s UNDECORATED goal) and re-read off the earliest row on later turns, so a run''s arm is stable. '
    'Frozen at insert by supervisor_decision_enforce_immutability.';

CREATE OR REPLACE FUNCTION supervisor_decision_reject_journal_mutations() RETURNS TRIGGER AS $$
BEGIN
    IF (TG_OP = 'DELETE') THEN
        RAISE EXCEPTION
            'supervisor_decision is permanent audit — DELETE rejected (run=%, sequence=%, kind=%).',
            OLD.supervisor_run_id, OLD.sequence, OLD.decision_kind;
    END IF;

    IF (NEW.payload_jsonb      IS DISTINCT FROM OLD.payload_jsonb
        OR NEW.sequence        IS DISTINCT FROM OLD.sequence
        OR NEW.decision_kind   IS DISTINCT FROM OLD.decision_kind
        OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
        OR NEW.team_id         IS DISTINCT FROM OLD.team_id
        OR NEW.supervisor_run_id IS DISTINCT FROM OLD.supervisor_run_id
        OR NEW.fence_epoch     IS DISTINCT FROM OLD.fence_epoch
        OR NEW.lesson_arm      IS DISTINCT FROM OLD.lesson_arm) THEN
        RAISE EXCEPTION
            'supervisor_decision journal + identity fields are frozen at insert — UPDATE of payload_jsonb/sequence/'
            'decision_kind/idempotency_key/team_id/supervisor_run_id/fence_epoch/lesson_arm rejected (run=%, sequence=%, kind=%). '
            'Only the status path (status/outcome_jsonb/error) is mutable.',
            OLD.supervisor_run_id, OLD.sequence, OLD.decision_kind;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
