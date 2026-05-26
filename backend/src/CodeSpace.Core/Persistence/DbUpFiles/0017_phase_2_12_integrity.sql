-- Phase 2.12 — Integrity hardening.
--
-- Five schema-level fixes, batched into one migration so reviewers see the whole
-- coherence pass at once. Greenfield-safe-only — assumes no production data exists yet
-- (matches the operating posture of 0014/0015/0016).
--
--   1. workflow_run.run_request_id → NOT NULL.
--      Phase 2.9 wired every run to flow through a workflow_run_request, but kept the
--      column nullable as a "legacy escape hatch". No producer ever leaves it null in
--      the new code paths. Locking it in eliminates the `?.SourceType ?? ""` footgun
--      and lets ListRunsAsync project source_type directly.
--
--   2. workflow_run_variable.run_id — drop CASCADE, replace with NO ACTION.
--      The intent (per WorkflowRunVariable.cs doc) is that the snapshot survives run
--      deletion for audit ("which historical runs ever resolved this secret name").
--      The old cascade contradicted that. The ledger's immutability trigger already
--      prevents workflow_run deletion in practice, but the schema should match intent.
--
--   3. workflow_run.team_id — add denormalised tenant column + idx_workflow_run_team_started.
--      Matches the pattern on Variable / WorkflowRunRequest / WorkflowArtifact. Cuts
--      one JOIN out of every team-scoped run query AND reduces the chance of a
--      forgotten WHERE-clause silently leaking cross-team rows.
--
--   4. workflow_run_node view — fix started_at ordering for failed-before-started nodes.
--      Pre-2.12 the view's `s.started_at` was NULL for cells that emitted only
--      `node.failed` (e.g. engine threw during input resolution before node.started
--      could be written). PG sorts NULLs LAST in ASC order → such nodes appeared at
--      the bottom of the run-detail timeline regardless of their actual position.
--      Fix: COALESCE the started_at fallback to the cell's earliest occurred_at across
--      any node.* record, so ordering reflects when the node actually entered the run.
--
--   5. workflow_run_variable.scope rename "Wf" → "Workflow" backfill.
--      Engine now writes "Workflow" (matching the canonical VariableScope enum); any
--      pre-2.12 dev rows still carrying "Wf" get updated so the replay path's WHERE
--      Scope = "Workflow" filter sees them.

-- ─── 1 + 3. workflow_run column changes ──────────────────────────────────────

-- Add team_id denormalised from workflow.team_id. Greenfield: no rows, so an UPDATE-from-JOIN
-- backfill is a no-op; included anyway so re-running on a dev DB with existing rows works.
ALTER TABLE workflow_run
    ADD COLUMN team_id UUID NULL;

UPDATE workflow_run r
SET team_id = w.team_id
FROM workflow w
WHERE r.workflow_id = w.id
  AND r.team_id IS NULL;

ALTER TABLE workflow_run
    ALTER COLUMN team_id SET NOT NULL,
    ADD CONSTRAINT fk_workflow_run_team
        FOREIGN KEY (team_id) REFERENCES team(id);

CREATE INDEX idx_workflow_run_team_started
    ON workflow_run(team_id, started_at DESC NULLS LAST);

COMMENT ON COLUMN workflow_run.team_id IS
    'Phase 2.12 — denormalised from workflow.team_id at insert time. Matches the pattern '
    'on Variable / WorkflowRunRequest / WorkflowArtifact so team-scoped queries don''t need '
    'to join workflow. Tenant boundary: every WHERE clause on workflow_run MUST include '
    'team_id = $current_team. Enforced by handler-level + service-level guards.';

-- Lock in NOT NULL on run_request_id. Greenfield: no nulls exist; the constraint succeeds
-- without backfill. If somehow a row has null (dev DB from before 2.9 wiring), the engine
-- can backfill via a one-off ad-hoc UPDATE — out of scope for this migration.
ALTER TABLE workflow_run
    ALTER COLUMN run_request_id SET NOT NULL;

COMMENT ON COLUMN workflow_run.run_request_id IS
    'Phase 2.12 — locked to NOT NULL. Every run traces back through exactly one '
    'workflow_run_request. The run-detail UI joins through here for source_type, '
    'actor metadata, raw payload.';

-- ─── 2. workflow_run_variable cascade removal ────────────────────────────────

ALTER TABLE workflow_run_variable
    DROP CONSTRAINT IF EXISTS workflow_run_variable_run_id_fkey,
    ADD CONSTRAINT workflow_run_variable_run_id_fkey
        FOREIGN KEY (run_id) REFERENCES workflow_run(id);
-- No ON DELETE clause → defaults to NO ACTION (block delete of a workflow_run that has
-- snapshot rows). Combined with workflow_run_record's immutability trigger, this means
-- runs are effectively un-deletable while their audit trail exists — by design.

COMMENT ON CONSTRAINT workflow_run_variable_run_id_fkey ON workflow_run_variable IS
    'Phase 2.12 — NO ACTION (was CASCADE pre-2.12). Snapshots are audit evidence; deleting '
    'a parent run must not cascade-purge the evidence. The ledger''s immutability trigger '
    'already blocks run deletion in practice; this matches the schema to the documented intent.';

-- ─── 5. Backfill workflow_run_variable.scope "Wf" → "Workflow" ──────────────

UPDATE workflow_run_variable
SET scope = 'Workflow'
WHERE scope = 'Wf';

COMMENT ON COLUMN workflow_run_variable.scope IS
    'Phase 2.12 — string discriminator matching VariableScope enum: "Workflow" | "Team" | '
    '"Input" | future scopes. Pre-2.12 engine wrote "Wf" here, which silently broke any '
    'diagnostic join against the source variable table; backfilled in this migration.';

-- ─── 4. workflow_run_node view — fix NULL started_at ordering ────────────────

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
    -- inputs come from the EARLIEST node.started — preserves retry semantics (rest of the
    -- ledger still shows the retry chain; the view's single-row projection picks first).
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
    -- Fallback ordering source for cells that have NO node.started (e.g. engine threw
    -- during input resolution and only node.failed was emitted). The cell still has SOME
    -- node.* record, so MIN(occurred_at) is well-defined.
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
    r.payload_json->>'error' AS error,
    -- Phase 2.12 — fallback to the cell's earliest record when node.started is absent.
    -- Keeps the run-detail UI's chronological ordering correct for failed-before-started cells.
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
    'Phase 2.10 + 2.12 — backward-compat projection over workflow_run_record. Returns the '
    'last-state per (run_id, node_id, iteration_key) by ranking node.* records sequence DESC. '
    'Phase 2.12 added the first_occurrence fallback for started_at so cells that emit only '
    'node.failed (engine crashed before node.started) still get a non-null timestamp for '
    'chronological ordering. The real source of truth is workflow_run_record; this view is '
    'for app-layer convenience + the run-detail UI''s grouped-by-node card view.';
