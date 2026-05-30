-- 0033_workflow_run_wait.sql
-- Workflow Engine v2 — Phase 1 (suspend / resume) mechanism.
--
-- When a node returns Suspended, the engine records ONE wait row capturing why the run is
-- paused and how it will be woken: a Timer (wake_at), a human Approval, or an external
-- Callback (both correlated by an opaque token). The run goes to status='Suspended' (0032)
-- and the engine returns. A resume signal resolves the matching wait (status='Resolved' +
-- payload), flips the run Suspended -> Pending, and re-dispatches; the durable walker (Phase 0)
-- rehydrates, injects the payload as the node's ResumePayload, and the node completes.
--
-- One outstanding wait per (run, node, iteration) — a node can be parked on at most one signal
-- at a time. The row is the source of truth the resume path mutates; the node.suspended ledger
-- record is the immutable audit copy.

CREATE TABLE workflow_run_wait (
    id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id          UUID         NOT NULL REFERENCES workflow_run(id) ON DELETE CASCADE,

    node_id         VARCHAR(128) NOT NULL,
    iteration_key   VARCHAR(128) NOT NULL DEFAULT '',

    -- Why the run is parked + how it will wake. Timer wakes itself (wake_at); Approval/Callback
    -- wake on an external signal correlated by token.
    wait_kind       VARCHAR(16)  NOT NULL CHECK (wait_kind IN ('Timer','Approval','Callback')),
    token           VARCHAR(128) NOT NULL,
    wake_at         TIMESTAMPTZ  NULL,        -- set for Timer; the scheduled resume fires at this instant

    status          VARCHAR(16)  NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Resolved')),
    payload_jsonb   JSONB        NULL,        -- the resume payload, set when the wait is resolved
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    resolved_at     TIMESTAMPTZ  NULL,

    -- At most one outstanding wait per node instance. Resuming resolves it; the engine never
    -- writes a second wait for the same (run, node, iteration) without the first being resolved.
    CONSTRAINT uq_wrw_run_node_iter UNIQUE (run_id, node_id, iteration_key)
);

-- Timer sweep / scheduled-resume lookup: due Pending timers.
CREATE INDEX idx_wrw_due_timer
    ON workflow_run_wait(wake_at)
    WHERE status = 'Pending' AND wait_kind = 'Timer';

-- Approval / callback resume: correlate an incoming token to its outstanding wait.
CREATE INDEX idx_wrw_pending_token
    ON workflow_run_wait(token)
    WHERE status = 'Pending';

COMMENT ON TABLE workflow_run_wait IS
    'Engine v2 Phase 1 — one row per node suspension. Captures the wait kind (Timer/Approval/'
    'Callback), the correlation token, the timer wake_at, and (once resolved) the resume payload. '
    'The resume path resolves the row + flips the run Suspended->Pending + re-dispatches; the '
    'durable walker injects payload_jsonb as the node''s ResumePayload on re-run. The immutable '
    'audit copy of each suspension is the node.suspended record in workflow_run_record.';
