-- 0027_drop_repository_project_id.sql
--
-- Phase 3.1b — drop the legacy repository.project_id column. Migration 0026
-- introduced the project_repository link table as the N:M source of truth and
-- the application code has fully migrated; this migration removes the now-
-- unused column.
--
-- No data migration: the seed step in 0025 + the dual-write in
-- RepositoryBindingService kept the column populated during the transition,
-- but operators are expected to re-attach repositories via the UI if they
-- need to preserve project membership beyond what the link table already
-- carries. There are no production deployments yet, so this is a safe drop.
--
-- Index cleanup is automatic: PostgreSQL drops any index defined on the
-- column (the EF-managed IX_repository_project_id from RepositoryConfiguration
-- and the named idx_repository_project_active from 0025) when the column
-- itself drops. No explicit DROP INDEX needed.
--
-- Idempotency: IF EXISTS guards re-runs on environments where the column was
-- already removed (e.g. schemaversions reset).

ALTER TABLE repository DROP COLUMN IF EXISTS project_id;

-- Refresh the table comment on project_repository: the prior text (set by 0026)
-- described the "during the 3.1 transition the legacy repository.project_id
-- column is dual-written" state. That transition is now complete, so re-state
-- the comment to match the steady-state schema. `COMMENT ON TABLE` is naturally
-- idempotent — it overwrites the previous value.
COMMENT ON TABLE project_repository IS
    'Phase 3.1 — N:M link between Project and Repository. A repository may belong '
    'to many projects (shared libraries, monorepo carving) and a project owns many '
    'repositories. team_id denormalised for tenancy filtering — must match both '
    'project.team_id and repository.team_id at write time (enforced in service).';
