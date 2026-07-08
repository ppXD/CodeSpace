-- 0096_repository_publish_mode.sql
--
-- Publish-or-park PR-2: the repo-level policy override in the publish guard chain (a fork-operator control, not an
-- env var — see PR-1's memory note on why env gates were removed). 'Branch' (default) is today's behaviour — a
-- non-empty diff pushes a branch by default. 'PatchOnly' is the escape hatch for a repository that must never
-- receive an agent-pushed branch (a protected/compliance-sensitive repo): the guard chain (RepositoryPolicyPublishGuard)
-- reads this and skips the push, recording WHY on the publish_manifest row's summary — the diff is still captured
-- and offloaded (I1 holds regardless). Additive: a brand-new column on an existing table, nothing else touched.
-- Idempotent (ADD COLUMN IF NOT EXISTS; the CHECK constraint has no IF NOT EXISTS in Postgres, but DbUp's own
-- journal already guarantees this script runs at most once — mirrors 0094_workflow_run_wait_infra_park_kind.sql's
-- plain ADD CONSTRAINT after a DROP IF EXISTS).

ALTER TABLE repository ADD COLUMN IF NOT EXISTS publish_mode TEXT NOT NULL DEFAULT 'Branch';

ALTER TABLE repository DROP CONSTRAINT IF EXISTS repository_publish_mode_check;

ALTER TABLE repository
    ADD CONSTRAINT repository_publish_mode_check
    CHECK (publish_mode IN ('Branch', 'PatchOnly'));
