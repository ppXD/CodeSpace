-- 0237_durable_retention_record_deadlines.sql
--
-- The quarantine marker for the two record planes that get a reaper in this change: cleanup receipts and budget
-- reservations.
--
-- WHY EACH PLANE NEEDS ITS OWN. The retention protocol has two independent waits — an age floor measured from the
-- record's own terminal instant, and a quarantine measured from the FIRST observation that nothing cites it. The
-- second wait only exists if it is written down; a sweep that decided "uncited" in memory and deleted on the same
-- tick has one wait, not two, whatever its code says. `agent_run_log_stream` got this in 0235; these two tables have
-- nowhere to record it at all.
--
-- WHY NOTHING ELSE IS NEEDED. Both cursors exclude a permanently-uncollectable record in the CLAIM query rather than
-- settling it — a pinned receipt, a reservation a model-call attempt still names — so the only rows that are claimed
-- and not collected are the ones waiting out this deadline. That is what makes one column enough here where the log
-- stream also needed its modification time: nothing else can occupy a batch slot for ever.
--
-- Both are nullable ADD COLUMNs with no default and no index: metadata-only, no table rewrite, and NULL is exactly
-- what an older binary writes and reads. `agent_run_cleanup_receipt` has no trigger at all, and `budget_reservation`
-- none either, so no guard has to learn about them.
--
-- Rollback: ALTER TABLE ... DROP COLUMN retain_until on both. Nothing reads either from an older binary.

ALTER TABLE agent_run_cleanup_receipt ADD COLUMN retain_until timestamptz NULL;
ALTER TABLE budget_reservation ADD COLUMN retain_until timestamptz NULL;

COMMENT ON COLUMN agent_run_cleanup_receipt.retain_until IS
    'The earliest instant this receipt may be reclaimed, written by the first sweep that found it settled, past its rule and cited by nobody. NULL means no sweep has proposed it.';
COMMENT ON COLUMN budget_reservation.retain_until IS
    'The earliest instant this reservation may be reclaimed, written by the first sweep that found it terminal, past its rule and cited by nobody. NULL means no sweep has proposed it.';
