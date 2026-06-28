-- 0090_agent_run_session_id.sql
--
-- P3.1a — promote the harness-native CLI session/thread id from agent_run.result_jsonb to a first-class column.
-- A rerun's CONTINUE decision (P3.2c) resolves the prior AgentRun for a lineage cell and reads its captured session
-- id to decide CONTINUE-vs-fresh + thread it back as `claude --resume <id>` / `codex exec resume <id>`. A column read
-- beats a per-row JSON probe. The id (Claude `session_id` / Codex `thread_id`) is captured by the harness into
-- AgentRunResult.SessionId, so it already rides in result_jsonb (camelCase `sessionId`, per AgentJson web options) —
-- promote it + backfill historical rows.
--
-- Nullable: an in-flight run, a pre-session CLI, or a run whose stream never reached its session-bearing line has
-- none. The producer writes it at completion (AgentRunService.CompleteAsync). Idempotent.
--
-- No dedicated index: the CONTINUE lookup is a correlated probe by workflow_run_id (already served by
-- idx_agent_run_workflow_run, 0039), with session_id a cheap heap recheck (a lineage cell has few agent runs) —
-- the same recheck tier agent_definition_id (0068) sits in. Promote a (workflow_run_id, session_id) composite WHEN
-- a hot CONTINUE surface ships, after profiling.

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS session_id text;

UPDATE agent_run
SET session_id = result_jsonb ->> 'sessionId'
WHERE session_id IS NULL
  AND result_jsonb ->> 'sessionId' IS NOT NULL;
