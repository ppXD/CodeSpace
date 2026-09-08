-- A permanently uncleanable terminal spool used to occupy the oldest LIMIT batch on every sweep and starve all
-- later local work. Keep the runner handle as the cleanup obligation, and persist bounded retry state separately so
-- failed rows yield until their database-time eligibility boundary. A new runner handle resets these fields.
ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS spool_cleanup_attempts integer NOT NULL DEFAULT 0;
ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS spool_cleanup_last_attempt_at timestamptz NULL;
ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS spool_cleanup_next_attempt_at timestamptz NULL;
ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS spool_cleanup_last_error_code varchar(64) NULL;

ALTER TABLE agent_run DROP CONSTRAINT IF EXISTS ck_agent_run_spool_cleanup_attempts;
ALTER TABLE agent_run ADD CONSTRAINT ck_agent_run_spool_cleanup_attempts CHECK (spool_cleanup_attempts >= 0);

CREATE INDEX IF NOT EXISTS ix_agent_run_spool_cleanup_due
    ON agent_run (spool_cleanup_attempts, spool_cleanup_next_attempt_at ASC NULLS FIRST, completed_at, id)
    WHERE runner_handle IS NOT NULL AND status NOT IN ('Queued', 'Running');
