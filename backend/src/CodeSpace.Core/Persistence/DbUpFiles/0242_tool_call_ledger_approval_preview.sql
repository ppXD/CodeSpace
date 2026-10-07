-- 0242_tool_call_ledger_approval_preview.sql
--
-- An agent tool call parked for a human's approval used to post a card naming only the run, the tool and the tool's
-- generic description: the reviewer could not see which repository, pull request, head commit or command they were
-- approving, and the ledger kept only a hash of the arguments. The handler now resolves the arguments server-side into a
-- redacted, bounded preview before parking, renders it on the card, and stamps it on the row in the same park CAS that
-- stamps the approval token. A merge's preview pins the head commit it showed; the approved call executes with that pin.
--
-- approval_target is the server-derived key of the call's target (a merge: its repository and pull request at the head and
-- base its card pinned). A target a reviewer rejected is not asked again in the same run, whatever inert or cosmetic
-- argument the agent changes; while one call on a target awaits a reviewer no other is parked on it; a rejection fails
-- every undecided call of the run on the target, and an approved one that has not run is not run.
--
-- Additive: two nullable columns, no backfill (a row parked before this has no preview and no target, and executes as it
-- always did). Idempotent (IF NOT EXISTS). An older pod ignores both columns. Every target lookup is scoped to one run and
-- rides the existing (team_id, agent_run_id, created_date, id) index.

ALTER TABLE tool_call_ledger ADD COLUMN IF NOT EXISTS approval_preview_jsonb jsonb NULL;

ALTER TABLE tool_call_ledger ADD COLUMN IF NOT EXISTS approval_target VARCHAR(200) NULL;

COMMENT ON COLUMN tool_call_ledger.approval_preview_jsonb IS
    'The redacted, bounded ToolCallPreview the approval card was built from: what the reviewer saw, and the pins the approved call executes with.';

COMMENT ON COLUMN tool_call_ledger.approval_target IS
    'Server-derived toolKind:sha256 key of the call target a reviewer approves or rejects; a rejected target is not asked again in the same run, and holds at most one live card.';
