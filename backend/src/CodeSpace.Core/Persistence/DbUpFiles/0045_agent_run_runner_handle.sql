-- 0045_agent_run_runner_handle.sql
--
-- Adds agent_run.runner_handle: the durable "runner handle" (a SandboxHandle as JSON — runner kind, the
-- supervisor pid, the on-disk spool directory, and the wall-clock deadline) recorded the instant a run is
-- launched on a durable sandbox runner. It lets a backend that restarts mid-run re-attach to (or recover)
-- the run from the persisted handle instead of abandoning it; the agent's stdout/stderr are spooled to that
-- directory independently of the launching process, so the output survives the launching process dying.
--
-- jsonb (not a typed column) so a future runner backend can record its own handle shape — and a later slice
-- can add a read offset for re-attach — without a migration. NULL until launched, and for runs on a
-- non-durable runner.
--
-- Additive + non-breaking: one nullable column on an existing table, nothing else touched. Idempotent.

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS runner_handle JSONB NULL;

COMMENT ON COLUMN agent_run.runner_handle IS
    'Durable runner handle (SandboxHandle as JSON: kind, supervisor pid, spool dir, deadline) recorded at '
    'launch so a restarted backend can re-attach to or recover the run from its spool. NULL until launched '
    'or for a non-durable runner.';
