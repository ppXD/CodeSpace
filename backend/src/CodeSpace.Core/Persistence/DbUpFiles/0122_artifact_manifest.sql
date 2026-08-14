-- DC-4 slice 1 (typed artifact manifest): a produced NON-GIT deliverable finally gets a durable, first-class
-- identity. Today an agent-written report/diagram survives only as a hunk inside the captured diff (repo-backed
-- runs) or not at all; the store row that holds its bytes is an untyped CAS bag whose meaning lives in the
-- REFERRING field's name. This table is the noun: one row per (attempt, declared path) naming WHAT kind of thing
-- was produced, WHERE its bytes live (workflow_artifact), and WHICH later capture superseded it.
--
-- Deliberately a SIBLING of capture_intent (attempt-grain key: agent_run_id + fence_epoch), NOT an extension of
-- publish_manifest — that table is structurally a GIT ledger (repository_alias is NOT NULL inside both unique
-- indices; branch/commit/patch are its only content axes) and widening its kind enum would silently change the
-- Room delivery reader, the Integrate stage trace, and the north-star IsSolved scan.
-- superseded_by_manifest_id copies capture_intent's #1352 discipline: a pointer, never a rewrite.
-- Rollback: DROP TABLE artifact_manifest;
CREATE TABLE artifact_manifest (
    id uuid PRIMARY KEY,
    team_id uuid NOT NULL,
    agent_run_id uuid NOT NULL,
    workflow_run_id uuid NULL,
    fence_epoch bigint NOT NULL DEFAULT 1,
    kind text NOT NULL,
    logical_path text NOT NULL,
    content_artifact_id uuid NOT NULL,
    sha256 text NOT NULL,
    size_bytes bigint NOT NULL,
    content_type text NOT NULL,
    superseded_by_manifest_id uuid NULL,
    created_date timestamptz NOT NULL,
    created_by uuid NOT NULL,
    last_modified_date timestamptz NOT NULL,
    last_modified_by uuid NOT NULL
);

-- Unique over CURRENT rows only: a changed re-capture APPENDS (history intact) and retires the prior row via
-- the supersession pointer — the partial predicate is what lets the chain grow while "one current per
-- (attempt, path)" stays machine-enforced.
CREATE UNIQUE INDEX ux_artifact_manifest_attempt_path ON artifact_manifest (agent_run_id, fence_epoch, logical_path) WHERE superseded_by_manifest_id IS NULL;
CREATE INDEX idx_artifact_manifest_workflow_run ON artifact_manifest (workflow_run_id) WHERE workflow_run_id IS NOT NULL;
