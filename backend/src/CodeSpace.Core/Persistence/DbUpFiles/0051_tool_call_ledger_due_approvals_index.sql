-- 0051_tool_call_ledger_due_approvals_index.sql
--
-- Item D3 (durable HITL reaper) performance fix. ExpireStaleApprovalsAsync sweeps for UNDECIDED approvals past their
-- deadline — Status == 'AwaitingApproval' AND approved_at IS NULL AND approval_deadline_at < now, ordered by
-- approval_deadline_at. Without an index that sweep is a FULL TABLE SCAN of tool_call_ledger every reaper tick; the
-- ledger grows unbounded (one row per side-effecting tool call across every run), so the scan cost climbs forever
-- while the matching set stays tiny (only the handful of currently-parked approvals).
--
-- A PARTIAL index over approval_deadline_at, restricted to exactly the reaper's candidate predicate
-- (status = 'AwaitingApproval' AND approved_at IS NULL), stays small (only undecided parked rows are indexed) and
-- serves both the predicate filter and the ORDER BY approval_deadline_at directly. Additive + non-breaking: a new
-- index, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE INDEX IF NOT EXISTS idx_tool_call_ledger_due_approvals
    ON tool_call_ledger (approval_deadline_at)
    WHERE status = 'AwaitingApproval' AND approved_at IS NULL;
