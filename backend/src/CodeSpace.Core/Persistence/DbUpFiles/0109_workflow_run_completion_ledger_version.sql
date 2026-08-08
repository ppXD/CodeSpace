-- 0109_workflow_run_completion_ledger_version.sql
--
-- P2 (v4.3, first slice): the run's MONOTONIC completion-ledger version. The shadow/terminal watermark gates
-- compare COUNTS (requirements, receipts, decisions, manifests, agent runs) — and counts are blind to IN-PLACE
-- writes: an amended requirement overwrites its row, a publish manifest's state transition is an ExecuteUpdate
-- on the same row. Both leave every count unchanged, so the reassessment gate reads "nothing moved" and a late
-- amendment can never reach the assessment. This column is bumped (atomic SQL increment) by every completion-
-- ledger writer whose write a count cannot see; the watermark carries it alongside the counts, so the equality
-- gates strengthen without any consumer change. The full P2 arc (compose-at-R, commit observations to R+1,
-- terminal CAS on Status && Revision && Generation) builds on this same column.
-- Rollback: ALTER TABLE workflow_run DROP COLUMN completion_ledger_version. Idempotent.

ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS completion_ledger_version BIGINT NOT NULL DEFAULT 0;
