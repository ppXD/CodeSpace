-- 0154_workflow_run_record_commit_cursor.sql
--
-- workflow_run_record.sequence began as a BIGSERIAL allocation token. A sequence value is
-- allocated before transaction commit, so transaction A could allocate N, wait, and commit
-- after transaction B had committed N+1. A live `sequence > cursor` reader that observed B
-- would then skip A forever.
--
-- Assign the token only after acquiring a transaction-scoped lock for the run. The lock is
-- retained until commit/rollback, so every later transaction for that run receives a larger
-- value only after the prior owner has settled. Runs remain independent and can write in
-- parallel. Values remain gapful (rollback consumes one) and are not a global commit cursor.

CREATE OR REPLACE FUNCTION workflow_run_record_assign_commit_cursor() RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.run_id::text, 154));
    NEW.sequence := nextval('workflow_run_record_sequence_seq'::regclass);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS workflow_run_record_assign_commit_cursor ON workflow_run_record;

CREATE TRIGGER workflow_run_record_assign_commit_cursor
    BEFORE INSERT ON workflow_run_record
    FOR EACH ROW
    EXECUTE FUNCTION workflow_run_record_assign_commit_cursor();

-- Avoid allocating and discarding a value before the trigger's run-scoped admission gate.
-- EF marks the column ValueGeneratedOnAdd and reads the trigger-assigned value via RETURNING.
ALTER TABLE workflow_run_record ALTER COLUMN sequence DROP DEFAULT;

COMMENT ON COLUMN workflow_run_record.sequence IS
    'Gapful run-scoped commit-admission cursor. Safe for sequence > cursor within one run; not a global commit cursor.';
