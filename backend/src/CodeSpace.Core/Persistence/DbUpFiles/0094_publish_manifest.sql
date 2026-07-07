-- 0094_publish_manifest.sql
--
-- The publish-or-park invariant's ledger (PR-1 of the publish-manifest arc): ONE durable, queryable row per git
-- artifact a supervisor subtask (kind=Agent) or a run-level integration (kind=Integration) produced — branch, commit
-- sha, the full patch (offloaded to workflow_artifact — see patch_artifact_id), changed-file list, acceptance state,
-- and publish state. This becomes the SINGLE SOURCE OF TRUTH a dependent subtask's workspace staging, the supervisor
-- decider prompt, the session room, and the next session turn all read — nobody guesses a branch name from
-- agent_run.result_jsonb again. Written REGARDLESS of the run's final status (Succeeded/Failed/TimedOut all get a row
-- when a workspace diff exists) — the "did the work leave a trace" question must never depend on how the run ended.
--
-- ux_publish_manifest_agent IS the idempotency lock a retry/reattach/reconciler re-run must respect: one row per
-- (agent_run_id, repository_alias), upserted (never re-inserted), so re-observing the SAME agent run's SAME repo
-- can only ever update this row, never mint a duplicate branch record. repository_alias (NOT NULL, defaults to
-- 'primary' for a single-repo run) is the key component — not repository_id, which mirrors
-- RepositoryRunResult.RepositoryId in being nullable (a workspace repo can be write-access without a resolved
-- catalog id) and would let multiple NULLs silently bypass a Postgres unique index.
--
-- ux_publish_manifest_integration is the run-level counterpart for kind=Integration rows (no agent_run_id — the
-- integration branch is a property of the WHOLE run, not one subtask).
--
-- Soft references throughout (no FK to agent_run/workflow_run/repository), matching agent_run.workflow_run_id and
-- tool_call_ledger.agent_run_id: this ledger outlives any of them. patch_artifact_id soft-links workflow_artifact
-- (offloaded via IArtifactOffloader, same primitive agent_run.session_transcript already offloads through).
-- changed_files_jsonb is the full path list (small: strings, not blobs) so no room/decider consumer is ever silently
-- capped — changed_file_count is a fast count alongside it. Additive: a brand-new table, nothing else touched.
-- Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS publish_manifest (
    id                    UUID         NOT NULL PRIMARY KEY,
    team_id               UUID         NOT NULL REFERENCES team(id),
    kind                  TEXT         NOT NULL,
    workflow_run_id       UUID         NULL,                         -- soft link, like agent_run.workflow_run_id
    agent_run_id          UUID         NULL,                         -- soft link; NULL for an Integration row
    repository_id         UUID         NULL,                         -- soft link; nullable like RepositoryRunResult.RepositoryId
    repository_alias      TEXT         NOT NULL DEFAULT 'primary',   -- the idempotency key component (see header) — never NULL
    base_sha              TEXT         NULL,
    branch                TEXT         NULL,
    commit_sha            TEXT         NULL,
    patch_artifact_id     UUID         NULL,                         -- soft link to workflow_artifact.id — the FULL untruncated patch
    changed_file_count    INTEGER      NOT NULL DEFAULT 0,
    changed_files_jsonb   JSONB        NULL,                         -- the full path list — never silently capped
    acceptance_state      TEXT         NOT NULL DEFAULT 'NotApplicable',
    publish_state         TEXT         NOT NULL DEFAULT 'None',
    publish_error         TEXT         NULL,
    summary               TEXT         NULL,
    created_date          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by            UUID         NOT NULL,
    last_modified_date    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    last_modified_by      UUID         NOT NULL
);

-- The idempotency lock for an agent-scoped row: one row per (agent run, repo). A retry / reattach / reconciler
-- re-observing the SAME agent run's SAME repo upserts this row — it can never mint a second branch record.
CREATE UNIQUE INDEX IF NOT EXISTS ux_publish_manifest_agent ON publish_manifest(agent_run_id, repository_alias) WHERE agent_run_id IS NOT NULL;

-- The idempotency lock for a run-level integration row: one row per (workflow run, repo).
CREATE UNIQUE INDEX IF NOT EXISTS ux_publish_manifest_integration ON publish_manifest(workflow_run_id, repository_alias) WHERE kind = 'Integration';

-- The room / decider / session-fold read path: every manifest row for a run, newest first.
CREATE INDEX IF NOT EXISTS idx_publish_manifest_workflow_run ON publish_manifest(workflow_run_id, created_date DESC) WHERE workflow_run_id IS NOT NULL;

COMMENT ON TABLE publish_manifest IS
    'The publish-or-park ledger: one durable row per git artifact an agent subtask or run-level integration '
    'produced (branch/commit/offloaded-patch/changed-files/acceptance/publish state), written REGARDLESS of the '
    'run''s final status. ux_publish_manifest_agent is the idempotency lock keyed on (agent_run_id, '
    'repository_alias) — a retry/reattach/reconciler re-run upserts, never duplicates. The single source of truth '
    'for dependent-subtask staging, the supervisor decider, the session room, and cross-turn fold.';
