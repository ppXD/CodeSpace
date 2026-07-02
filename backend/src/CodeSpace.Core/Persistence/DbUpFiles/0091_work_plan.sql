-- 0091_work_plan.sql
--
-- The durable plan artifact (Planning/Agent/Evaluation triad, slice S1): one row per plan VERSION per run.
-- Two producers write it — the plan.author graph node and the supervisor's plan decision — so the
-- confirmation gate (S3) and the run-detail checklist (S2) read ONE producer-agnostic store instead of
-- re-parsing producer-specific tapes.
--
-- The row holds the CONTRACT only (goal + items_json with per-item dependsOn/acceptance). Execution state
-- (which agent ran an item, acceptance verdicts) stays on the already-durable tape (agent_run +
-- supervisor_decision folds) and is joined at read time — one source of truth, replay-deterministic.
--
-- workflow_run_id is a SOFT reference (no FK), mirroring agent_run.workflow_run_id: plan artifacts are
-- managed independently of the run row's lifecycle (cross-aggregate). team_id keeps its FK to team (the
-- stable root), denormalized for team-scoped reads like every other run-ish table.
--
-- (workflow_run_id, version) is the append-only version sequence; the partial unique index on
-- (workflow_run_id, origin_key) is the exactly-once guard for replayable producers (the supervisor's
-- per-turn key) — producers that WANT a new version every time (plan.author edit loop) write NULL.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS work_plan (
    id                    UUID         NOT NULL PRIMARY KEY,
    team_id               UUID         NOT NULL REFERENCES team(id),
    workflow_run_id       UUID         NOT NULL,
    version               INTEGER      NOT NULL,
    status                TEXT         NOT NULL DEFAULT 'Authored',
    origin_kind           TEXT         NOT NULL,
    origin_key            TEXT         NULL,
    goal                  TEXT         NOT NULL DEFAULT '',
    items_json            JSONB        NOT NULL DEFAULT '[]',
    success_criteria_json JSONB        NULL,
    risks_json            JSONB        NULL,
    created_at            TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT ix_work_plan_workflow_run_id_version UNIQUE (workflow_run_id, version)
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_work_plan_workflow_run_id_origin_key
    ON work_plan (workflow_run_id, origin_key) WHERE origin_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_work_plan_team_id ON work_plan (team_id);
