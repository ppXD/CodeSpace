-- 0065_workflow_run_scope_arrays.sql
--
-- Launch-time SCOPE arrays for repo/project run filtering. A run is MULTI-repo (WorkspaceSpec.Repositories), so a
-- scalar column won't do — denormalise the repo set and its derived project set as uuid[] arrays ON workflow_run,
-- with a GIN index on each so an array-overlap filter ("runs touching ANY of these repos") is an index probe, JOIN-free
-- (the same denorm-over-join strategy as source_type 0062). The OR-within-a-field semantics map directly to `&&`.
--
-- SEMANTICS — these are the run's LAUNCH SCOPE (which repos/projects the task was launched against), a point-in-time
-- snapshot: a run "belonged to project Y when it ran", even if the repo is later re-homed. They are NOT the repos the
-- run actually TOUCHED / changed — that is a distinct dimension the future Changes projector will produce as
-- touched_repository_ids / changed_repository_ids. Do not conflate task scope with change result.
--
-- Populated at launch for snapshot / task runs (the multi-repo launch path); authored workflow runs have no launch-time
-- repo set (their repos live in the workflow definition) and keep the empty-array default. Idempotent.

ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS scope_repository_ids uuid[] NOT NULL DEFAULT '{}';
ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS scope_project_ids    uuid[] NOT NULL DEFAULT '{}';

CREATE INDEX IF NOT EXISTS idx_workflow_run_scope_repository_ids ON workflow_run USING GIN (scope_repository_ids);
CREATE INDEX IF NOT EXISTS idx_workflow_run_scope_project_ids    ON workflow_run USING GIN (scope_project_ids);

COMMENT ON COLUMN workflow_run.scope_repository_ids IS
    'Launch-time SCOPE: the repositories this run was launched against (multi-repo), a point-in-time snapshot — NOT the '
    'repos the run actually touched (that is the future touched_repository_ids). uuid[] + GIN for array-overlap filter.';
