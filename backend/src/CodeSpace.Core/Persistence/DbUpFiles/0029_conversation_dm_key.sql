-- 0029_conversation_dm_key.sql
--
-- Race-safe 1-on-1 DM identity. A DM between two users must be a SINGLETON — opening a
-- DM with someone you already DM'd returns the existing conversation, never a second one.
-- Two clients opening the same DM concurrently would otherwise both INSERT and create
-- duplicates.
--
-- dm_key is a deterministic, order-independent key derived from the member pair
-- ({minUserId}:{maxUserId}). The partial unique index turns "find-or-create" into an
-- atomic INSERT … ON CONFLICT-style operation: the service inserts and, on a 23505 unique
-- violation, re-queries the winner. Same 23505-tolerance pattern the ingestion auditor uses.
--
-- Only Direct conversations set dm_key; channel / group leave it null (the partial index's
-- WHERE excludes them, so they never collide). Additive + nullable ⇒ non-breaking.
--
-- Idempotent: IF NOT EXISTS guards a re-run.

ALTER TABLE conversation ADD COLUMN IF NOT EXISTS dm_key TEXT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_conversation_dm_key
    ON conversation(team_id, dm_key)
    WHERE dm_key IS NOT NULL AND deleted_date IS NULL;

COMMENT ON COLUMN conversation.dm_key IS
    'Direct-message identity: {minUserId}:{maxUserId} (sorted, order-independent). Null for '
    'channel / group. The partial unique index on (team_id, dm_key) makes DM find-or-create '
    'race-safe — concurrent opens collide on the index and the loser re-queries the winner.';
