-- 0020_repository_webhook_lifecycle.sql
--
-- Outbox demolition (Phase 2.16) — make repository_webhook itself the durable
-- registration ledger. Pre-2.16, BindAsync wrote a generic outbox_message row carrying
-- a "RegisterWebhook" payload and the actual RepositoryWebhook row only landed AFTER
-- the dispatcher succeeded at the remote. The indirection bought nothing — there is
-- exactly one outbox MessageType in production (RegisterWebhook), so a generic
-- outbox_message table is over-abstraction for a single-use-case scenario.
--
-- 2.16 collapses the indirection (mirroring the 2.15 workflow_run change): the
-- RepositoryWebhook row is inserted in 'Pending' state in the SAME EF SaveChanges as
-- the Repository row, then a dedicated dispatcher CAS-flips Pending → Enqueued and
-- hands the id to Hangfire. A dedicated worker (IRepositoryWebhookRegistrar) CAS-flips
-- Enqueued → Registering, makes an IDEMPOTENT provider call (check by callback URL
-- first, register only if absent), and writes external_id + Registered on success.
--
-- The dual CAS gives the same no-double-execution guarantee as workflow_run:
--   • Two dispatchers cannot both flip Pending → Enqueued (rows-affected = 1 wins, 0 loses).
--   • Two Hangfire retries cannot both flip Enqueued → Registering.
--   • The provider call is idempotent by callback URL even if the worker DID double-fire.
--
-- This migration:
--   1. Adds the lifecycle columns (status, attempts, next_attempt_at, last_error,
--      enqueued_at, registering_at, registered_at).
--   2. Makes external_id nullable — only known after Registered state is reached.
--   3. Replaces the (repository_id, external_id) unique constraint with a partial unique
--      that only enforces uniqueness once registered (so multiple non-terminal rows for
--      the same repo + a previous Cancelled row don't collide on NULL external_id).
--   4. Adds a partial index on the non-terminal states for the reconciler's "stuck X"
--      scans + the dispatcher's "due Pending" scan.

-- ─── 1. Lifecycle columns ──────────────────────────────────────────────────
ALTER TABLE repository_webhook
    ADD COLUMN registration_status TEXT NOT NULL DEFAULT 'Pending',
    ADD COLUMN attempts            INT  NOT NULL DEFAULT 0,
    ADD COLUMN next_attempt_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ADD COLUMN last_error          TEXT NULL,
    ADD COLUMN enqueued_at         TIMESTAMPTZ NULL,
    ADD COLUMN registering_at      TIMESTAMPTZ NULL,
    ADD COLUMN registered_at       TIMESTAMPTZ NULL;

-- Existing rows pre-date the lifecycle model and are by definition already Registered
-- on the remote (the old code path only ever inserted RepositoryWebhook AFTER the remote
-- provider call succeeded). Backfill them to Registered with registered_at = created_date
-- so the partial unique index below catches any duplicates and the reconciler skips them.
UPDATE repository_webhook
SET registration_status = 'Registered',
    registered_at       = created_date
WHERE registration_status = 'Pending';

ALTER TABLE repository_webhook ADD CONSTRAINT repository_webhook_status_check
    CHECK (registration_status IN ('Pending','Enqueued','Registering','Registered','Failed','DeadLettered','Cancelled'));

COMMENT ON COLUMN repository_webhook.registration_status IS
    'Phase 2.16 — registration lifecycle. Pending → Enqueued → Registering → Registered '
    '(happy), or → Failed → Pending (retry), or → DeadLettered (exhausted), or → Cancelled '
    '(unbind raced an in-flight registration). Dispatcher + worker advance via atomic CAS.';

COMMENT ON COLUMN repository_webhook.attempts IS
    'Number of times the registrar has tried + failed. Reset on each successful Registered '
    'transition (re-binds reuse the same row). Capped by MaxAttempts in the worker — past '
    'that the row goes to DeadLettered.';

COMMENT ON COLUMN repository_webhook.next_attempt_at IS
    'Earliest time the dispatcher should reconsider this row. Set by the worker after a '
    'failed attempt (now + backoff). Reconciler''s Pending scan filters by this column so '
    'a row backing off doesn''t flood the dispatcher.';

-- ─── 2. external_id nullable ───────────────────────────────────────────────
-- Old shape: external_id NOT NULL, populated synchronously inside BindAsync.
-- New shape: external_id NULL until the worker reaches Registered state. The provider
-- assigns the id at registration time; we cannot know it before the call returns.
ALTER TABLE repository_webhook ALTER COLUMN external_id DROP NOT NULL;

COMMENT ON COLUMN repository_webhook.external_id IS
    'Phase 2.16 — provider-side webhook id. NULL until registration_status = Registered. '
    'Populated atomically with the Registering → Registered CAS so any reader that sees '
    'Registered is guaranteed to see a non-null external_id.';

-- ─── 3. Replace unique constraint with partial unique on Registered rows ────
-- Old constraint was UNIQUE (repository_id, external_id) — that's still the production
-- invariant we want (no two registered hooks pointing at the same provider id under one
-- repo), but it must not fire for non-terminal rows where external_id is NULL. Postgres
-- treats NULLs in unique constraints as distinct so the bare constraint would technically
-- allow infinitely many NULL-external_id rows; making it partial on Registered also
-- documents the invariant ("only one active registered hook per repo + external_id").
ALTER TABLE repository_webhook DROP CONSTRAINT repository_webhook_repository_id_external_id_key;

CREATE UNIQUE INDEX repository_webhook_registered_unique
    ON repository_webhook(repository_id, external_id)
    WHERE registration_status = 'Registered';

COMMENT ON INDEX repository_webhook_registered_unique IS
    'Phase 2.16 — once a webhook is Registered with a provider-side id, there must be at '
    'most one row per (repository, external_id). Partial so non-terminal rows (Pending / '
    'Enqueued / Registering with NULL external_id) don''t conflict and a previous Cancelled '
    'row doesn''t block a re-bind from succeeding.';

-- ─── 4. Index for the reconciler + dispatcher hot paths ─────────────────────
-- Three scans hit this table at recurring-job cadence:
--   • Dispatcher / reconciler: "find Pending rows where next_attempt_at <= now()".
--   • Reconciler: "find Enqueued rows where last_modified_date < threshold" (stuck enqueue).
--   • Reconciler: "find Registering rows where registering_at < threshold" (worker crashed
--     between CAS and provider response).
-- Single partial index on the non-terminal status set with next_attempt_at as the secondary
-- key covers all three — terminal rows (Registered/DeadLettered/Cancelled) are excluded so
-- the index stays small even as a tenant accumulates thousands of hooks over time.
CREATE INDEX idx_repository_webhook_active
    ON repository_webhook(registration_status, next_attempt_at)
    WHERE registration_status IN ('Pending','Enqueued','Registering','Failed');

COMMENT ON INDEX idx_repository_webhook_active IS
    'Phase 2.16 — partial index on non-terminal webhook-registration states. Supports the '
    'dispatcher''s "due Pending" scan + the reconciler''s "stuck Enqueued / Registering" + '
    '"due Failed" scans. Terminal rows excluded so the index size tracks the in-flight '
    'queue depth, not the lifetime hook count.';
