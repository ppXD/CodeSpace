-- 0093_work_session_conversation_id.sql
--
-- Triad S4a: give launched supervisor runs a chat surface. `work_session.conversation_id` links the thread
-- to the channel its HITL cards (ask_human, plan confirmation, approvals) post into — created lazily on the
-- first supervisor-tier launch, reused by every later turn of the same session. SOFT reference (no FK, same
-- stance as work_plan.workflow_run_id): deleting the channel must not break the session; the executor's
-- tenancy check degrades and the next ensure re-creates.

ALTER TABLE work_session ADD COLUMN IF NOT EXISTS conversation_id uuid NULL;
