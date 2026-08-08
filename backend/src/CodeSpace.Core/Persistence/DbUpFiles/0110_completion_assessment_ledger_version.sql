-- 0110_completion_assessment_ledger_version.sql
--
-- P2 (v4.3, second slice): the SQL-comparable half of the staleness predicate. 0109 gave every run a monotonic
-- ledger version; this records, on each assessment row, the version its compose actually read (captured AFTER the
-- compose, so the compose's own write-through receipts are inside it — the A2 watermark discipline). The shadow
-- sweep's revisit pass then becomes one indexed comparison — head.version > latest assessment's ledger_version —
-- replacing the 24-hour CompletedAt window, which was both too wide (re-examining every recent run each sweep)
-- and too narrow (a manifest settling on day 3 was silently out of reach forever).
-- NULL = a pre-slice row: it compares stale once, reassesses, and converges.
-- Rollback: ALTER TABLE completion_assessment DROP COLUMN ledger_version. Idempotent.

ALTER TABLE completion_assessment ADD COLUMN IF NOT EXISTS ledger_version BIGINT;
