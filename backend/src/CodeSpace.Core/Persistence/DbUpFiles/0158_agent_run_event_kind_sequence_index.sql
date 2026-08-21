-- 0158_agent_run_event_kind_sequence_index.sql
--
-- A filtered Agent Run event audit pages one exact open kind with the same sequence keyset as the
-- general timeline. Keeping kind between the run identity and cursor lets Postgres seek matching
-- ToolCall rows directly instead of walking every Reasoning/token event and filtering afterward.
--
-- DbUp runs migrations in a transaction, so CONCURRENTLY is unavailable under the current runner.
-- IF NOT EXISTS keeps local/replayed upgrades idempotent.

CREATE INDEX IF NOT EXISTS idx_are_run_kind_sequence
    ON agent_run_event (agent_run_id, kind, sequence);

COMMENT ON INDEX idx_are_run_kind_sequence IS
    'Bounded exact-kind Agent Run event audit: run + open kind discriminator + sequence keyset.';
