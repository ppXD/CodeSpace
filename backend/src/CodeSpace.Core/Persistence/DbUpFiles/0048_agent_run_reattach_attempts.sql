-- 0048_agent_run_reattach_attempts.sql
--
-- Adds agent_run.reattach_attempts: how many times the reconciler has re-claimed this run for a LIVE re-attach
-- (its detached process is alive but its worker vanished after a restart). It is incremented in the SAME atomic
-- UPDATE as each reclaim (ReclaimForReattachAsync), so the counter can never lag the action — once it reaches
-- the reconciler's cap, a still-unattachable-but-alive run is abandoned rather than reclaimed forever (the
-- no-livelock guarantee). Counting a best-effort breadcrumb event instead would let the counter stall while the
-- reclaim keeps succeeding, so the ceiling must hang off a hard column written in the reclaim's own transaction.
--
-- Additive + non-breaking: one NOT NULL column defaulting to 0 (every existing run starts un-re-attached).
-- Idempotent (IF NOT EXISTS).

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS reattach_attempts INTEGER NOT NULL DEFAULT 0;

COMMENT ON COLUMN agent_run.reattach_attempts IS
    'Reconciler live-re-attach reclaim count, incremented atomically in each reclaim. Bounds re-attach attempts: '
    'past the cap a still-unattachable-but-alive run is abandoned, never reclaimed forever.';
