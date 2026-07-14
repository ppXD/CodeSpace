-- 0104_budget_reservation.sql
--
-- W-hard (v4.2-FINAL): the ATOMIC budget reservation ledger -- the transactional inversion of the pure-fold
-- realized-spend check whose documented weakness is one-wave overshoot (a K-agent wave admits pre-spawn, spends
-- mid-flight past the cap). State machine, seven states, text-stored:
--   Reserved -> InFlight -> Settled | Released | Expired | Indeterminate -> Reconciled
-- THE invariant every admission enforces under a per-run advisory lock: settled + live reservations <= hard cap
-- (live = Reserved|InFlight|Indeterminate). Settlement is PESSIMISTIC: an unknown actual settles AT the reserved
-- amount, never lower -- only a Reconciled pass may adjust down. price_version stamps which price table valued the
-- reservation so a later price change never silently re-values history. parent_reservation_id builds hierarchical
-- accounts (a wave under a run, an agent under a wave). ux_budget_reservation is the idempotency lock: one row per
-- (run, kind, scope_key) -- a crash-replayed producer lands on its own row.
-- Rollback: DROP TABLE budget_reservation;
-- Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS budget_reservation (
    id                      UUID        NOT NULL PRIMARY KEY,
    team_id                 UUID        NOT NULL REFERENCES team(id),
    workflow_run_id         UUID        NOT NULL,
    parent_reservation_id   UUID        NULL,
    kind                    TEXT        NOT NULL,
    scope_key               TEXT        NOT NULL,
    state                   TEXT        NOT NULL,
    reserved_usd            NUMERIC(12,4) NOT NULL,
    settled_usd             NUMERIC(12,4) NULL,
    price_version           TEXT        NOT NULL,
    expires_at              TIMESTAMPTZ NULL,
    created_date            TIMESTAMPTZ NOT NULL,
    created_by              UUID        NOT NULL,
    last_modified_date      TIMESTAMPTZ NOT NULL,
    last_modified_by        UUID        NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_budget_reservation ON budget_reservation (workflow_run_id, kind, scope_key);
CREATE INDEX IF NOT EXISTS ix_budget_reservation_team ON budget_reservation (team_id);
CREATE INDEX IF NOT EXISTS ix_budget_reservation_state ON budget_reservation (state, expires_at);
