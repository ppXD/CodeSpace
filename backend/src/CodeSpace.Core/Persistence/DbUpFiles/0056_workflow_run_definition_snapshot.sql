-- 0056_workflow_run_definition_snapshot.sql
--
-- Dynamic-workflows substrate (PR1). The durable engine must be able to run a workflow_run whose
-- definition is an INLINE FROZEN SNAPSHOT carried by the run itself — with NO workflow row and NO
-- workflow_version row. A one-shot AI task is a RUN with its own definition, not a persisted /
-- listable workflow.
--
-- Two complementary changes:
--   1. Relax the (workflow_id, workflow_version) coupling so a run can exist WITHOUT a parent
--      workflow / version. Both columns become NULL-able and the two FK constraints that pinned a
--      run to a workflow(id) + workflow_version(workflow_id, version) are dropped. Authored runs
--      keep populating both columns exactly as before (byte-identical INSERT); only the schema
--      stops REQUIRING them, so a snapshot run can leave them NULL.
--   2. Add the inline snapshot pair: definition_snapshot_jsonb (the frozen WorkflowDefinition the
--      engine deserialises + walks) + definition_snapshot_hash (the same SHA-256 canonical hash a
--      workflow_version row carries, so the engine's tamper-check is identical for both sources).
--
-- The engine forks on definition_snapshot_jsonb IS NULL: NULL → the existing (workflow_id, version)
-- workflow_version lookup, byte-identical; non-NULL → deserialise + hash-check the inline snapshot.
-- Everything downstream (walk, suspend/resume, rehydrate) operates on the loaded WorkflowDefinition
-- regardless of source.
--
-- Additive + non-breaking: two NULL columns, two columns relaxed to NULL, two FKs dropped (the FK
-- was an INVARIANT for authored runs — authored runs still satisfy it, they just aren't forced to
-- by the DB). team_id stays NOT NULL (every run is team-scoped, snapshot or not). Idempotent
-- (IF EXISTS / IF NOT EXISTS). No existing row changes.

-- 1a. Drop the two FK constraints that required a parent workflow / version. Postgres auto-named
--     them on the inline REFERENCES in 0009_workflows.sql; drop by the conventional name with
--     IF EXISTS so a re-run (or a future-renamed constraint) is a no-op.
ALTER TABLE workflow_run DROP CONSTRAINT IF EXISTS workflow_run_workflow_id_fkey;
ALTER TABLE workflow_run DROP CONSTRAINT IF EXISTS workflow_run_workflow_id_workflow_version_fkey;

-- 1b. Relax the NOT NULL on the workflow-pin columns. A snapshot run carries neither.
ALTER TABLE workflow_run ALTER COLUMN workflow_id DROP NOT NULL;
ALTER TABLE workflow_run ALTER COLUMN workflow_version DROP NOT NULL;

-- 2. The inline frozen definition + its canonical hash. NULL for every authored run (which loads
--    its definition from workflow_version); non-NULL for a snapshot run.
ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS definition_snapshot_jsonb JSONB NULL;
ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS definition_snapshot_hash  TEXT  NULL;

COMMENT ON COLUMN workflow_run.workflow_id IS
    'Parent workflow id for an AUTHORED run (workflow + workflow_version → run). NULL for a snapshot '
    'run, which carries its own definition_snapshot_jsonb instead of pointing at a persisted workflow.';

COMMENT ON COLUMN workflow_run.definition_snapshot_jsonb IS
    'Dynamic-workflows substrate — the inline FROZEN WorkflowDefinition this run executes, when the run '
    'is NOT backed by a workflow_version. NULL for authored runs (they load definition_json from their '
    'pinned workflow_version). When non-NULL the engine deserialises + walks this JSON directly and '
    'creates NO workflow / workflow_version row.';

COMMENT ON COLUMN workflow_run.definition_snapshot_hash IS
    'SHA-256 canonical hash of definition_snapshot_jsonb (same DefinitionHash.Compute used for '
    'workflow_version.definition_hash). The engine recomputes + compares it at load time; a mismatch '
    'throws the same tamper exception as a drifted authored version. NULL for authored runs.';
