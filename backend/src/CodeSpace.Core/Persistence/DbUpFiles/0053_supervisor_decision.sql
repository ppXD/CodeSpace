-- 0053_supervisor_decision.sql
--
-- PR-E E1 — the durable, exactly-once, replayable ledger of a SUPERVISOR's decisions (pure substrate). Mirrors
-- 0049_tool_call_ledger: the UNIQUE (supervisor_run_id, idempotency_key) index IS the exactly-once invariant — a racing
-- duplicate INSERT hits it and the loser reads the winner's row (the dedup path) instead of re-executing the decision.
-- The idempotency key is SERVER-derived (`decision_kind` + ":" + SHA-256 of the canonical payload, + a caller turn
-- discriminator; see SupervisorDecisionLog.DeriveIdempotencyKey) — NEVER read from the model — so a model cannot forge
-- it to replay an old decision or defeat dedup; the key already binds the payload hash, so a different payload is a
-- different key (never silently collapsed).
--
-- `team_id` keeps its FK to team (tenancy on EVERY row, team-scoped queries); `supervisor_run_id` is a deliberate SOFT
-- reference (no FK, like agent_run_event) — the ledger outlives its run row. `sequence` is a per-run BIGSERIAL cursor
-- giving the replay tape its natural ordering. `fence_epoch` mirrors agent_run.fence_epoch at claim time and is recorded
-- for AUDIT/forensics — the single-winner guarantee is FIRST-WRITER-WINS on `status` (the status-guarded CAS
-- transitions, esp. the Pending → Running claim BEFORE the side effect), NOT an epoch comparison.
--
-- The JOURNAL fields (payload_jsonb, sequence, decision_kind, idempotency_key) are FROZEN at insert; the STATUS PATH
-- (status, outcome_jsonb, error) is the deliberately-mutable CAS path. A BEFORE UPDATE OR DELETE trigger enforces that
-- split (mirrors 0015's append-only pattern, refined to a frozen-vs-CAS column split). The `AwaitingApproval` status is
-- reserved for a later HITL slice — unused by E1.
--
-- PURE SUBSTRATE: nothing writes this table until the supervisor node/loop wiring lands (E2). A brand-new table,
-- nothing else touched. Additive + non-breaking. Idempotent (IF NOT EXISTS / OR REPLACE / guarded trigger create).

CREATE TABLE IF NOT EXISTS supervisor_decision (
    id                    UUID         NOT NULL PRIMARY KEY,
    team_id               UUID         NOT NULL REFERENCES team(id),
    supervisor_run_id     UUID         NOT NULL,                 -- soft link (no FK), like agent_run_event
    sequence              BIGSERIAL    NOT NULL,                 -- per-run replay cursor (DB-assigned)
    decision_kind         TEXT         NOT NULL,                 -- open string (plan|spawn|retry|ask_human|merge|stop|…) — zero churn
    idempotency_key       TEXT         NOT NULL,                 -- server-derived; UNIQUE per run
    input_hash            VARCHAR(64)  NOT NULL,                 -- VARCHAR (not CHAR) to match EF HasMaxLength(64) — no blank-pad surprise
    status                TEXT         NOT NULL,
    payload_jsonb         JSONB        NOT NULL,                 -- the emitted decision — FROZEN at insert (journal field)
    outcome_jsonb         JSONB        NULL,                     -- the execution result — CAS-mutable
    error                 TEXT         NULL,                     -- terminal failure reason — CAS-mutable
    fence_epoch           BIGINT       NOT NULL DEFAULT 0,
    created_date          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by            UUID         NOT NULL,
    last_modified_date    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by      UUID         NOT NULL
);

-- The exactly-once invariant: one row per (run, idempotency key). A racing duplicate INSERT hits this and the loser
-- reads the winner's row (the dedup path, see SupervisorDecisionLog.TryClaimAsync). The key already binds the payload
-- hash, so a different payload is a different key — never silently collapsed.
CREATE UNIQUE INDEX IF NOT EXISTS ux_supervisor_decision_run_key ON supervisor_decision(supervisor_run_id, idempotency_key);

-- The replay tape: a run's decisions in BIGSERIAL order. Every replay/audit read traverses this index.
CREATE INDEX IF NOT EXISTS idx_sd_run_sequence ON supervisor_decision(supervisor_run_id, sequence);

-- ─── Frozen-vs-CAS immutability trigger ────────────────────────────────────────
-- The JOURNAL fields are frozen-at-insert; the status path is the deliberately-mutable CAS path. A DB-layer trigger
-- makes that contract uncircumventable from the app layer — including bugs that try to "fix" a decision payload via a
-- tracking-bug update. DELETE is rejected outright (audit is permanent). An UPDATE is allowed ONLY when every journal
-- field is unchanged — so the status-guarded CAS (status / outcome_jsonb / error / last_modified_date) proceeds, but
-- any attempt to mutate payload_jsonb, sequence, decision_kind, or idempotency_key is rejected.

CREATE OR REPLACE FUNCTION supervisor_decision_reject_journal_mutations() RETURNS TRIGGER AS $$
BEGIN
    IF (TG_OP = 'DELETE') THEN
        RAISE EXCEPTION
            'supervisor_decision is permanent audit — DELETE rejected (run=%, sequence=%, kind=%).',
            OLD.supervisor_run_id, OLD.sequence, OLD.decision_kind;
    END IF;

    IF (NEW.payload_jsonb     IS DISTINCT FROM OLD.payload_jsonb
        OR NEW.sequence       IS DISTINCT FROM OLD.sequence
        OR NEW.decision_kind  IS DISTINCT FROM OLD.decision_kind
        OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key) THEN
        RAISE EXCEPTION
            'supervisor_decision journal fields are frozen at insert — UPDATE of payload_jsonb/sequence/decision_kind/'
            'idempotency_key rejected (run=%, sequence=%, kind=%). Only the status path (status/outcome_jsonb/error) is mutable.',
            OLD.supervisor_run_id, OLD.sequence, OLD.decision_kind;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS supervisor_decision_enforce_immutability ON supervisor_decision;
CREATE TRIGGER supervisor_decision_enforce_immutability
    BEFORE UPDATE OR DELETE ON supervisor_decision
    FOR EACH ROW EXECUTE FUNCTION supervisor_decision_reject_journal_mutations();

COMMENT ON TABLE supervisor_decision IS
    'PR-E E1 — durable exactly-once + replayable ledger of a supervisor''s decisions (pure substrate). '
    'ux_supervisor_decision_run_key is the exactly-once invariant: a racing duplicate INSERT loses and reads the '
    'winner''s row. idempotency_key is SERVER-derived (decision_kind + SHA-256 of canonical payload + turn discriminator) '
    '— never from the model. The single-winner guarantee is first-writer-wins on status (the Pending → Running claim CAS '
    'before the side effect). The journal fields (payload_jsonb, sequence, decision_kind, idempotency_key) are frozen at '
    'insert; the status path (status, outcome_jsonb, error) is the deliberately-mutable CAS path — enforced by the '
    'supervisor_decision_enforce_immutability trigger. AwaitingApproval is reserved for a later HITL slice.';
