-- The team Runs index (WorkflowService.TeamRunsQuery / CollapseToLatestPerLineage) shows EVERY WorkflowRun for the
-- team, including task/snapshot runs (null workflow_id) — there was no discriminator between a genuine operator
-- launch and an internal instrument's own launch (e.g. a TaskLaunch qualification/benchmark cell, which launches
-- through the real ITaskLaunchService as the team's own borrowed Owner). purpose is an open marker: NULL (the
-- overwhelming default) is a genuine launch; a non-null value excludes the run from the team Runs index by default
-- (see WorkflowRunPurposes.cs). Partial index: the column is sparse (almost every row is NULL).
ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS purpose text NULL;
CREATE INDEX idx_workflow_run_purpose ON workflow_run (purpose) WHERE purpose IS NOT NULL;
