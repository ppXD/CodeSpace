-- 0021_drop_outbox.sql
--
-- Outbox demolition closeout (Phase 2.16). Migration 0020 moved the single remaining
-- outbox use-case (webhook registration) onto the RepositoryWebhook row's own lifecycle
-- state machine. No code path still writes outbox_message — drop the table.
--
-- A rollback to a pre-2.16 image MUST run 0003_outbox.sql again to re-create the table,
-- and BindAsync would need to be reverted to the old "insert outbox_message" path. The
-- code currently in the tree compiles only against the new RepositoryWebhook lifecycle,
-- so this migration is part of the same atomic unit (one PR) as the code change.

DROP TABLE IF EXISTS outbox_message;
