-- 0237_durable_retention_record_deadlines.sql
--
-- The quarantine marker for the record plane that gets a reaper in this change: settled cleanup receipts.
--
-- WHY EACH PLANE NEEDS ITS OWN. The retention protocol has two independent waits — an age floor measured from the
-- record's own terminal instant, and a quarantine measured from the FIRST observation that nothing cites it. The
-- second wait only exists if it is written down; a sweep that decided "uncited" in memory and deleted on the same
-- tick has one wait, not two, whatever its code says. `agent_run_log_stream` got this in 0235; this table has nowhere
-- to record it at all.
--
-- WHY ONE COLUMN IS ENOUGH. The cursor excludes a permanently-uncollectable receipt — a pinned one, an orphan — in the
-- CLAIM query rather than settling it, so the only rows claimed and not collected are the ones waiting out a deadline
-- this column holds. That is what makes one column enough here where the log stream also needed its modification time:
-- nothing else can occupy a batch slot for ever.
--
-- It is a nullable ADD COLUMN with no default: metadata-only, no table rewrite, and NULL is exactly what an older
-- binary writes and reads. `agent_run_cleanup_receipt` carries no trigger, so no guard has to learn about it.
--
-- THE INDEX takes a SHARE lock for the length of its build — concurrent reads continue, concurrent INSERTs wait. It is
-- built in-script rather than CONCURRENTLY because DbUp runs each script in one transaction and CONCURRENTLY cannot
-- appear inside one; the table holds a handful of rows per abandoned run, so the build is short. Its shape is the
-- claim query's exactly: the per-team head of the settled queue, oldest first.
--
-- Rollback: DROP INDEX ix_agent_run_cleanup_receipt_retention; ALTER TABLE agent_run_cleanup_receipt DROP COLUMN
-- retain_until. Nothing reads either from an older binary.

ALTER TABLE agent_run_cleanup_receipt ADD COLUMN retain_until timestamptz NULL;

CREATE INDEX ix_agent_run_cleanup_receipt_retention
    ON agent_run_cleanup_receipt (team_id, recorded_at, id)
    WHERE outcome IN ('Completed', 'Compensated');

COMMENT ON COLUMN agent_run_cleanup_receipt.retain_until IS
    'The earliest instant this receipt may be reclaimed, written by the first sweep that found it settled, past its rule and cited by nobody. NULL means no sweep has proposed it.';
