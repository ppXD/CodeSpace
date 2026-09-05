-- What the sandbox ACTUALLY did for this run, stamped at launch: bubblewrap applied (and whether egress was
-- severed), or unconfined with the reason the host could not confine (not-linux / no-bwrap / no-userns). Its own
-- column rather than a field inside runner_handle, because the spool reaper NULLS runner_handle 24h after a run goes
-- terminal — and the posture a run had is a permanent fact about it, not a recovery aid. NULL for runs launched
-- before this column existed and for runners that record no posture; readers must keep the old hedged wording there
-- rather than guess.
ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS sandbox_confinement jsonb NULL;
