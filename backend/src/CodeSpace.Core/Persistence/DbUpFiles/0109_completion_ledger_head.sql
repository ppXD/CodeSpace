-- 0109_completion_ledger_head.sql
--
-- P2 (v4.3, first slice): the run's MONOTONIC completion-ledger version. The shadow/terminal watermark gates
-- compare COUNTS (requirements, receipts, decisions, manifests, agent runs) — and counts are blind to IN-PLACE
-- writes: an amended requirement overwrites its row, a publish manifest's state transition is an ExecuteUpdate
-- on the same row. Both leave every count unchanged, so the reassessment gate reads "nothing moved" and a late
-- amendment can never reach the assessment.
--
-- Its own table ON PURPOSE, not a workflow_run column: the run row carries an xmin concurrency token and is
-- TRACKED by the engine for the whole turn — a side-writer's UPDATE there aborts the engine's own save with a
-- concurrency conflict (proven live: run 31230952188 flipped supervisor runs to Failure). A one-row side table
-- gives writers an atomic upsert-increment with zero shared-row contention; the future P2 terminal CAS reads it
-- through a subquery in its own conditional UPDATE.
-- Rollback: DROP TABLE completion_ledger_head. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS completion_ledger_head (
    workflow_run_id     UUID        NOT NULL PRIMARY KEY,
    version             BIGINT      NOT NULL
);
