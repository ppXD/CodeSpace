-- 0066_workflow_run_actor_id.sql
--
-- Denormalise the launching actor (actor_id) from workflow_run_request ONTO workflow_run so "runs launched by X" is a
-- filter on a run column, not a join — mirroring the source_type denorm (0062). NULLABLE: a webhook / system run has
-- no user actor (workflow_run_request.actor_id is itself nullable, set NULL for ActorType=webhook). Backfilled from
-- the request; populated at the two run-creation sites (RunStarter from the envelope, RunFromSnapshotStarter from the
-- actor param) going forward. The actor filter is recheck-tier on the team keyset index (no dedicated
-- (team_id, actor_id, created_date DESC, id DESC) index until an actor-filtered surface proves hot — same stance as
-- status / source). Idempotent (IF NOT EXISTS + null-guarded backfill).

ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS actor_id uuid;

UPDATE workflow_run r
SET actor_id = req.actor_id
FROM workflow_run_request req
WHERE r.run_request_id = req.id
  AND r.actor_id IS NULL;

COMMENT ON COLUMN workflow_run.actor_id IS
    'Denormalised from workflow_run_request.actor_id at row insert — the user who launched the run. NULL for a '
    'webhook / system run with no user actor. Lets the runs index filter by launcher without joining the request.';
