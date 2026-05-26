-- 0015_run_record_ledger.sql
-- Phase 2.10 — generic append-only lifecycle ledger.
--
-- Today the engine writes one row per (run × node × iter) into workflow_run_node, capturing
-- only the FINAL state. Retries, intermediate state, external API calls, log lines, and any
-- other observability event are invisible. Replay-time debugging is poor; the run-detail UI
-- can only show "this node succeeded" not "this node tried 3 times, the GitHub API 502'd
-- twice, the third attempt returned a 200 with this body".
--
-- Phase 2.10 introduces workflow_run_record: an append-only event journal that captures
-- EVERY interesting lifecycle event during a run. record_type is an OPEN STRING so new
-- event kinds (node.suspended, external_call.retrying, llm.token, etc.) add zero schema
-- churn. The old workflow_run_node table is dropped and recreated as a VIEW that projects
-- the latest-state per (run, node, iter) from the ledger — existing consumers (run-detail
-- UI, integration tests asserting on node rows) keep working unchanged.
--
-- Greenfield ops: no row migration needed. The integration-test fixture recreates its DB.

-- ─── 1. workflow_run_record — the ledger ──────────────────────────────────────

CREATE TABLE workflow_run_record (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    -- NO ON DELETE CASCADE: the ledger is permanent audit. Deleting a workflow_run row is
    -- blocked by this FK while records exist; operators with a legitimate purge need (GDPR
    -- right-to-erasure, dev-only fixture reset) must explicitly DROP+RECREATE the table or
    -- run a privileged operation that bypasses the immutability trigger via a session var.
    run_id          UUID NOT NULL REFERENCES workflow_run(id),

    -- Sequence is the ledger's natural per-run ordering. BIGSERIAL guarantees monotonic
    -- INSERT order globally, which is enough for replay reconstruction (one engine writes
    -- to one run at a time). Globally unique not strictly necessary but harmless.
    sequence        BIGSERIAL NOT NULL,

    -- Event discriminator. Open string — no CHECK constraint — so new event kinds drop in
    -- without a migration. Convention: dotted namespace ("node.*", "external_call.*",
    -- "iteration.*", "log") for grep-ability. The canonical set lives in
    -- WorkflowRunRecordTypes (C#).
    record_type     TEXT NOT NULL,

    -- The node this event pertains to. NULL for run-level events (a log line about the
    -- run itself, the scope-snapshot persistence event, etc.).
    node_id         TEXT NULL,

    -- Iteration key for nodes inside flow.iterate. Empty string for non-iteration nodes;
    -- matches the previous workflow_run_node convention so the view projects identically.
    iteration_key   TEXT NOT NULL DEFAULT '',

    -- Correlation id groups related records — e.g. external_call.started + external_call.completed
    -- share a correlation id so the UI can pair request with response. NULL when no grouping
    -- relationship applies.
    correlation_id  UUID NULL,

    -- Optional self-FK for hierarchical records (an attempt nested under its parent node row,
    -- a retry under its previous attempt). Most records don't set this.
    parent_record_id UUID NULL REFERENCES workflow_run_record(id) ON DELETE SET NULL,

    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Record-type-specific shape. Canonical payloads per record_type:
    --
    --   node.started:           {"inputs": {...}, "config": {...}}
    --   node.completed:         {"outputs": {...}, "duration_ms": N}
    --   node.failed:            {"error": "...", "outputs": {}, "duration_ms": N}
    --   node.skipped:           {"reason": "all-incoming-dead" | ...}
    --   iteration.started:      {"item_count": N}
    --   iteration.completed:    {"item_count": N, "duration_ms": N}
    --   external_call.started:  {"target": "https://...", "method": "POST", "request_artifact_id": "..."}
    --   external_call.completed: {"status": 200, "response_artifact_id": "...", "duration_ms": N}
    --   external_call.failed:   {"target": "...", "error": "...", "duration_ms": N}
    --   log:                    {"level": "info"|"warn"|"error", "message": "..."}
    payload_json    JSONB NOT NULL DEFAULT '{}'::jsonb
);

-- ─── Indexes ──────────────────────────────────────────────────────────────────

-- Run-scoped chronological scan: every run-detail UI read traverses this index.
CREATE INDEX idx_wrr_run_sequence ON workflow_run_record(run_id, sequence);

-- Per-node lookup: project the latest state per (run, node, iter). Used by the view below.
CREATE INDEX idx_wrr_run_node ON workflow_run_record(run_id, node_id, iteration_key)
    WHERE node_id IS NOT NULL;

-- Correlation lookup: "show me every record sharing this correlation id" (request/response,
-- attempt chain). Partial because most records carry NULL correlation_id.
CREATE INDEX idx_wrr_correlation ON workflow_run_record(correlation_id)
    WHERE correlation_id IS NOT NULL;

-- Type filter: "show all external calls in this run" / "show all failures". Compound with
-- run_id because every realistic query scopes to one run.
CREATE INDEX idx_wrr_run_type ON workflow_run_record(run_id, record_type);

-- ─── Append-only immutability trigger ──────────────────────────────────────────
-- The ledger is append-only by contract. UPDATE / DELETE on an existing record breaks the
-- replay-integrity guarantee that "what the engine saw at time T is what we still see now".
-- A DB-layer trigger makes that contract uncircumventable from the app layer — including
-- bugs that try to "fix" a stale record via a tracking-bug update.
--
-- The trigger fires on EVERY UPDATE/DELETE including cascade-induced ones. Combined with
-- the no-cascade FK above, this means records survive their parent run forever — exactly
-- the right semantic for an audit trail. A workflow_run row can only be deleted after its
-- records are explicitly removed via a privileged operation (DROP TABLE / CREATE TABLE in
-- dev, dedicated session-var bypass in prod tooling).

CREATE OR REPLACE FUNCTION workflow_run_record_reject_mutations() RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION
        'workflow_run_record is append-only — UPDATE/DELETE rejected (run=%, sequence=%, type=%). '
        'Insert a new record instead.',
        OLD.run_id, OLD.sequence, OLD.record_type;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_record_enforce_immutability
    BEFORE UPDATE OR DELETE ON workflow_run_record
    FOR EACH ROW EXECUTE FUNCTION workflow_run_record_reject_mutations();

COMMENT ON TABLE workflow_run_record IS
    'Phase 2.10 — append-only lifecycle ledger. Every interesting event during a workflow_run lands here. '
    'record_type is an open string for forward-compat (new event kinds add zero schema churn). '
    'Consumers project the ledger back into convenient shapes via SQL views (workflow_run_node) or '
    'app-side aggregation. Immutability enforced by trigger.';

-- ─── 2. workflow_run_node — DROP table, recreate as VIEW ──────────────────────
-- The view is a backward-compat reader so existing queries (WorkflowService.GetRunAsync,
-- integration tests asserting on node rows) keep working unchanged. The "last record_type
-- wins" semantics: a node's status is determined by its most-recent node.* record.

DROP TABLE workflow_run_node;

CREATE VIEW workflow_run_node AS
WITH ranked AS (
    -- For each (run, node, iter) cell, rank node.* records most-recent-first by sequence.
    -- rn_latest = 1 is the most recent record — its record_type maps to the cell's status.
    SELECT
        run_id, node_id, iteration_key, record_type, occurred_at, payload_json, sequence,
        ROW_NUMBER() OVER (
            PARTITION BY run_id, node_id, iteration_key
            ORDER BY sequence DESC
        ) AS rn_latest
    FROM workflow_run_record
    WHERE record_type LIKE 'node.%' AND node_id IS NOT NULL
),
first_started AS (
    -- The EARLIEST node.started for the cell carries the inputs the engine resolved at
    -- start time. Re-starts (retries) emit a fresh node.started; we surface the first so
    -- the UI shows "when did the node first begin". Retries are still visible in the ledger.
    SELECT
        run_id, node_id, iteration_key,
        MIN(occurred_at) AS started_at,
        (SELECT payload_json->'inputs'
         FROM workflow_run_record sub
         WHERE sub.run_id = wrr.run_id
           AND sub.node_id = wrr.node_id
           AND sub.iteration_key = wrr.iteration_key
           AND sub.record_type = 'node.started'
         ORDER BY sub.sequence ASC
         LIMIT 1) AS inputs_jsonb
    FROM workflow_run_record wrr
    WHERE record_type = 'node.started' AND node_id IS NOT NULL
    GROUP BY run_id, node_id, iteration_key
)
SELECT
    r.run_id,
    r.node_id,
    r.iteration_key,
    CASE r.record_type
        WHEN 'node.started'   THEN 'Running'
        WHEN 'node.completed' THEN 'Success'
        WHEN 'node.failed'    THEN 'Failure'
        WHEN 'node.skipped'   THEN 'Skipped'
        WHEN 'node.suspended' THEN 'Suspended'
        ELSE 'Pending'
    END AS status,
    COALESCE(s.inputs_jsonb, '{}'::jsonb) AS inputs_jsonb,
    COALESCE(r.payload_json->'outputs', '{}'::jsonb) AS outputs_jsonb,
    r.payload_json->>'error' AS error,
    s.started_at AS started_at,
    CASE
        WHEN r.record_type IN ('node.completed', 'node.failed', 'node.skipped')
        THEN r.occurred_at
        ELSE NULL
    END AS completed_at
FROM ranked r
LEFT JOIN first_started s
    ON r.run_id = s.run_id
   AND r.node_id = s.node_id
   AND r.iteration_key = s.iteration_key
WHERE r.rn_latest = 1;

COMMENT ON VIEW workflow_run_node IS
    'Phase 2.10 — backward-compat projection over workflow_run_record. Returns the last-state '
    'per (run_id, node_id, iteration_key) by ranking node.* records by sequence DESC. The '
    'real source of truth is workflow_run_record; this view is for app-layer convenience and '
    'the run-detail UI. Inputs come from the FIRST node.started for the cell; outputs/error '
    'come from the LATEST node.completed/failed/skipped.';
