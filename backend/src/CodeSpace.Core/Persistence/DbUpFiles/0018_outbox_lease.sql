-- 0018_outbox_lease.sql
--
-- Phase 2.13d — Outbox claim/lease (multi-worker safety).
--
-- The pre-2.13d dispatcher scans Pending rows with a plain SELECT then UPDATEs one by one.
-- Two API replicas running simultaneously can BOTH pick up the same row before either
-- writes status=Completed → the row's handler runs twice. For idempotent handlers (webhook
-- subscription) this is annoying; for SIDE-EFFECTING handlers (POST a PR comment, send a
-- Slack message) this is a correctness bug — the operator sees a duplicate effect.
--
-- The canonical Postgres fix: claim N rows atomically via UPDATE...RETURNING with
-- SKIP LOCKED. One worker wins the row; concurrent workers skip past it without blocking.
--
-- This migration adds three columns to track the claim:
--   - claimed_by:  worker identity (Guid) that holds the lease
--   - claimed_at:  when the lease was issued
--   - lease_until: when the lease expires; a reaper resets stale rows back to Pending
--
-- And one new OutboxStatus value: 'Claimed' (between Pending and Completed in the lifecycle).

ALTER TABLE outbox_message
    ADD COLUMN claimed_by  UUID,
    ADD COLUMN claimed_at  TIMESTAMPTZ,
    ADD COLUMN lease_until TIMESTAMPTZ;

COMMENT ON COLUMN outbox_message.claimed_by IS
    'Phase 2.13d — worker identity (Guid) holding the lease while status=Claimed. NULL when '
    'status=Pending/Completed/DeadLettered. Diagnostic-only; the actual concurrency guarantee '
    'comes from SKIP LOCKED on the claim UPDATE.';

COMMENT ON COLUMN outbox_message.claimed_at IS
    'Phase 2.13d — when the current lease was issued. Pair with lease_until to compute remaining time.';

COMMENT ON COLUMN outbox_message.lease_until IS
    'Phase 2.13d — when the current lease expires. Reaper job (OutboxLeaseReaper) resets rows '
    'with status=Claimed AND lease_until < now() back to status=Pending so a crashed worker''s '
    'in-flight message gets retried. Without a reaper, a worker crash freezes the row forever.';

-- The dispatcher's hot path is now: SELECT Pending OR (Claimed AND lease expired). Pre-2.13d
-- index only covered Pending; widen it so the reaper's scan is also indexed.
DROP INDEX idx_outbox_pending_due;

CREATE INDEX idx_outbox_due
    ON outbox_message (next_attempt_date, created_date)
    WHERE status = 'Pending';

-- Reaper-side index: find Claimed rows whose lease expired.
CREATE INDEX idx_outbox_lease_expired
    ON outbox_message (lease_until)
    WHERE status = 'Claimed';

COMMENT ON INDEX idx_outbox_lease_expired IS
    'Phase 2.13d — supports OutboxLeaseReaper''s scan for stale claims. Partial on '
    'status=Claimed because Pending/Completed/DeadLettered rows have NULL lease_until.';
