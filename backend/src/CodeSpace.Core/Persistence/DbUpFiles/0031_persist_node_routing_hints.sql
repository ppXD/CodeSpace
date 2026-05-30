-- 0031_persist_node_routing_hints.sql
-- Workflow Engine v2 — Phase 0 (durable walker groundwork).
--
-- Branch nodes (logic.if today; logic.switch later) choose which output handle(s) to follow
-- and return them as NodeResult.RoutingHints. Until now those hints lived ONLY in the engine's
-- in-memory WalkerState, so they were lost on a crash / restart. The durable, re-entrant walker
-- (Phase 0) must rebuild edge-liveness from persisted state WITHOUT re-running the branch node
-- (re-running a branch could double a side effect or pick a different path), so the chosen
-- handles have to live on the ledger.
--
-- The engine now writes routingHints into the node.completed record payload. This migration
-- redefines the workflow_run_node view to surface them as routing_hints_jsonb — NULL for the
-- common follow-all case (non-branch nodes never write routingHints). Purely additive: every
-- existing column is unchanged; this only ADDS one column to the read-only projection.

DROP VIEW workflow_run_node;

CREATE VIEW workflow_run_node AS
WITH ranked AS (
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
    -- inputs come from the EARLIEST node.started — preserves retry semantics (the ledger
    -- still shows the retry chain; the view's single-row projection picks first).
    SELECT
        run_id, node_id, iteration_key,
        MIN(occurred_at) AS first_started_at,
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
),
first_occurrence AS (
    -- Fallback ordering source for cells that have NO node.started (e.g. engine threw during
    -- input resolution and only node.failed was emitted).
    SELECT
        run_id, node_id, iteration_key,
        MIN(occurred_at) AS first_occurred_at
    FROM workflow_run_record
    WHERE record_type LIKE 'node.%' AND node_id IS NOT NULL
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
    -- Phase 0 (Engine v2): a branch node's chosen output handles, surfaced from the
    -- node.completed payload. NULL when the node didn't branch (follow all outgoing edges).
    r.payload_json->'routingHints' AS routing_hints_jsonb,
    r.payload_json->>'error' AS error,
    -- Phase 2.12 — fallback to the cell's earliest record when node.started is absent.
    COALESCE(s.first_started_at, f.first_occurred_at) AS started_at,
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
LEFT JOIN first_occurrence f
    ON r.run_id = f.run_id
   AND r.node_id = f.node_id
   AND r.iteration_key = f.iteration_key
WHERE r.rn_latest = 1;

COMMENT ON VIEW workflow_run_node IS
    'Engine v2 Phase 0 + Phase 2.10/2.12 — backward-compat projection over workflow_run_record. '
    'Last-state per (run_id, node_id, iteration_key) by ranking node.* records sequence DESC. '
    'routing_hints_jsonb surfaces a branch node''s chosen output handles (from the node.completed '
    'payload) so the durable walker rebuilds edge-liveness on re-entry; NULL = follow all edges. '
    'The real source of truth is workflow_run_record; this view is a read-only convenience projection.';
