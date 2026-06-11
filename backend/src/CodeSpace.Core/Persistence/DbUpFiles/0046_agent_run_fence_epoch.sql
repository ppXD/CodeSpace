-- 0046_agent_run_fence_epoch.sql
--
-- Adds agent_run.fence_epoch: a monotonic fencing token bumped on every claim (Queued/Running → Running).
-- A worker remembers the epoch it claimed with; CompleteAsync's status-guarded CAS additionally requires
-- that epoch, so a STALE worker whose run was reclaimed (a lease-expiry reclaim or a restart re-claim — each
-- bumps the epoch) and then revived matches 0 rows and loses its terminal write cleanly (no double-completion).
--
-- This is the safety substrate the lease-based reclaim (a follow-up) and the durable runner's restart
-- re-attach both depend on: without it, shortening the reclaim window — or re-attaching a second observer —
-- would trade a recovery-latency bug for a double-completion bug.
--
-- Additive + non-breaking: one NOT NULL column defaulting to 0 (every existing run starts at epoch 0).
-- Idempotent (IF NOT EXISTS).

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS fence_epoch BIGINT NOT NULL DEFAULT 0;

COMMENT ON COLUMN agent_run.fence_epoch IS
    'Monotonic fencing token bumped on each claim; the completion CAS requires the epoch the worker claimed '
    'with, so a reclaimed-then-revived worker loses its terminal write (no double-completion). The substrate '
    'for lease-based reclaim + durable restart re-attach.';
