-- 0003_outbox.sql
-- Transactional outbox for external side effects. The bind flow used to register
-- the remote webhook BEFORE committing the local DB transaction, which leaked orphan
-- webhooks if the DB write failed mid-flight. With this table, the bind flow commits
-- a "RegisterWebhook" message atomically with the Repository row, and a dispatcher
-- drains the outbox afterwards — retrying on transient remote failures and dead-lettering
-- on terminal ones.

CREATE TABLE outbox_message (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    aggregate_type       TEXT NOT NULL,
    aggregate_id         UUID NOT NULL,
    message_type         TEXT NOT NULL,
    payload              JSONB NOT NULL,
    status               TEXT NOT NULL DEFAULT 'Pending',
    attempts             INT NOT NULL DEFAULT 0,
    last_attempted_date  TIMESTAMPTZ,
    last_error           TEXT,
    next_attempt_date    TIMESTAMPTZ NOT NULL DEFAULT now(),

    created_date         TIMESTAMPTZ NOT NULL,
    created_by           UUID NOT NULL,
    last_modified_date   TIMESTAMPTZ NOT NULL,
    last_modified_by     UUID NOT NULL
);

-- Partial index for the dispatcher's hot path: pick due Pending rows in created order.
-- Completed and DeadLettered rows accumulate but never re-enter the scan range.
CREATE INDEX idx_outbox_pending_due
    ON outbox_message (next_attempt_date, created_date)
    WHERE status = 'Pending';

-- Diagnostic lookup: "which messages are stuck on a specific aggregate"
CREATE INDEX idx_outbox_aggregate ON outbox_message (aggregate_type, aggregate_id);
