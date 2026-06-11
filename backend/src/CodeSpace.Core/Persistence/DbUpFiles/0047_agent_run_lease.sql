-- 0047_agent_run_lease.sql
--
-- Adds agent_run.lease_expires_at: a DB-owned lease the claiming worker renews on its heartbeat. The
-- reconciler reclaims a Running run whose lease has LAPSED (lease_expires_at < now) — GROUND-TRUTH liveness
-- (a live worker keeps its own lease fresh) rather than inferring death from heartbeat-silence at query time.
-- The lease equals the liveness Window and the heartbeat renews it every Window/3, so a live worker's lease
-- never expires (two pings can be lost first). NULL until first claimed, and for runs claimed by an older
-- binary (treated as lapsed, like a null heartbeat). The fencing epoch (0046) makes a reclaim safe — a
-- reclaimed-then-revived worker loses its completion.
--
-- Additive + non-breaking: one nullable column. Idempotent (IF NOT EXISTS).

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS lease_expires_at TIMESTAMPTZ NULL;

COMMENT ON COLUMN agent_run.lease_expires_at IS
    'DB-owned lease the claiming worker renews on its heartbeat; the reconciler reclaims a Running run whose '
    'lease has lapsed (< now). Ground-truth liveness, not heartbeat-silence inference. NULL until claimed.';
