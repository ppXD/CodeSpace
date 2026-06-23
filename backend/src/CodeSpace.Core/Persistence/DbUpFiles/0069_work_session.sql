-- 0069_work_session.sql
--
-- The WorkSession layer (S1) — a long-term WORK-CONTEXT thread that sits ABOVE the pure WorkflowRun:
--   WorkSession (one thread) → WorkflowRun (one auditable/replayable execution = one turn) → AgentRun (work unit).
--
-- The session row stays thin: it owns the thread's IDENTITY (title / kind / lifecycle) + its rolling CONTEXT
-- (summary / scope); the turns, decisions, artifacts, cost, and branches all live on the runs. A run binds to its
-- session by an FK-FREE pointer column on workflow_run (workflow_run.session_id) — the SAME bare-lineage stance as
-- workflow_run.parent_run_id — NOT a join table, so a run ∈ exactly one session and the binding also rides the
-- existing run→agent_run FK to every child unit.
--
-- Migration shape (pure additive, no destructive ops — mirrors 0066's idempotency + 0022's table style):
--   1. Create `work_session` table + its team-listing index.
--   2. Add `workflow_run.session_id` + `workflow_run.session_turn_index` (both NULLABLE — default NULL = a
--      session-less run = byte-identical to pre-session behaviour). Like 0066's actor_id, these carry NO dedicated
--      index yet: in S1 every row is NULL so nothing queries them; the session-timeline index
--      (runs WHERE session_id = X ORDER BY session_turn_index) lands with the slice that introduces that query.

-- ─── 1. work_session table ──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS work_session (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                     UUID NOT NULL REFERENCES team(id),

    title                       VARCHAR(256) NOT NULL,

    -- Product semantic of the thread (Task / Pr / Issue / Workflow / Schedule / Custom) — NOT the trigger of any
    -- run inside it. Stored as the enum NAME (HasConversion<string>); no CHECK so adding a kind is zero schema churn.
    kind                        VARCHAR(16) NOT NULL,

    -- Lifecycle ONLY (Open / Archived) — never a run status. Live execution state is projected from runs + decisions.
    status                      VARCHAR(16) NOT NULL,

    -- Reserved durable thread context (rolling summary + scope, e.g. the per-repo branch-continuity map). NULL until
    -- the context/policy slices populate them; the columns land now so the table is the definitive shape.
    scope_jsonb                 JSONB NULL,
    summary                     TEXT NULL,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL
);

-- "List a team's sessions, newest first" — the session-index access path. created_date DESC serves both the
-- all-sessions and open-only views (status is a cheap recheck-tier filter), mirroring 0022's idx_project_team.
CREATE INDEX IF NOT EXISTS idx_work_session_team
    ON work_session(team_id, created_date DESC);

COMMENT ON TABLE work_session IS
    'WorkSession layer (S1) — a long-term work-context thread; one auditable WorkflowRun per turn binds via the '
    'FK-free workflow_run.session_id pointer. The row owns thread identity (title/kind/lifecycle) + rolling '
    'context (summary/scope); turns/decisions/artifacts/cost/branches live on the runs.';

-- ─── 2. workflow_run session pointers (nullable; default NULL = byte-identical) ──
ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS session_id uuid;
ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS session_turn_index integer;

COMMENT ON COLUMN workflow_run.session_id IS
    'FK-free pointer (same stance as parent_run_id) to the owning work_session — the long-term thread this run is one '
    'turn of. NULL for a session-less run (every run until the session layer binds them). Written at the two '
    'run-creation sites from a pre-resolved SessionAssignment.';

COMMENT ON COLUMN workflow_run.session_turn_index IS
    '1-based ordinal of this run within its session — ONLY a top-level user-visible turn gets one. A '
    'child/sub-workflow/replay/rerun inherits session_id but consumes NO new turn (attaches via parent_run_id), so its '
    'turn index stays NULL. NULL also whenever session_id is NULL.';
