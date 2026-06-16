-- 0057_agent_run_event_data_artifact.sql
--
-- D2 #1: offload a LARGE agent-event structured payload (data_json) to the content-addressed artifact store,
-- keeping only a reference here. A big tool_result / reasoning block (D3) would otherwise bloat this append-only
-- row unboundedly. The producer (AppendEventsAsync) routes data_json through the shared IArtifactOffloader: a
-- small payload stays inline in data_json (the common case, unchanged); a payload over the inline threshold is
-- written to workflow_artifact and data_json is nulled, with this column holding the artifact id. The read path
-- surfaces the ref so a consumer fetches the full payload on demand (GET /api/artifacts/{id}).
--
-- No FK: like agent_run_id, the event log is permanent audit that can outlive coupling — a dangling ref simply
-- resolves to null (GetBytesAsync returns null), never a crash, so event retention isn't chained to artifact
-- retention. Additive + nullable: every existing row keeps its inline data_json (data_artifact_id NULL).
-- Idempotent. ADD COLUMN is DDL, so the append-only UPDATE/DELETE immutability trigger is unaffected.

ALTER TABLE agent_run_event ADD COLUMN IF NOT EXISTS data_artifact_id UUID NULL;

COMMENT ON COLUMN agent_run_event.data_artifact_id IS
    'D2 #1 — when data_json was offloaded (payload over the artifact inline threshold), the workflow_artifact id '
    'holding the full structured payload; data_json is then NULL. NULL when the payload is inline (small) or absent.';
