-- 0241_pack_sealed_clone_url.sql
--
-- A pack imported from a pasted git URL that embedded a token stored that URL verbatim in pack.url, which every team
-- member (Viewers included) reads and the Library renders as a link. From now on pack.url holds the URL with its
-- userinfo removed, and the URL exactly as cloned is sealed with the platform's credential encryptor
-- (IPayloadEncryptor) in encrypted_clone_url, which Sync and import-from-pack decrypt just in time and nothing returns.
--
-- SQL cannot run that encryptor, so existing rows are sealed by the application: PackCloneUrlBackfillRecurringJob
-- rewrites each one with a conditional UPDATE, and a re-import of the same repository seals the row it lands in. Until
-- then, Sync keeps working on an unsealed row (its url still carries the token it needs), and the read model strips
-- userinfo on the way out.
--
-- duplicate_of_pack_id marks a legacy fork: the same repository imported once with a token and once without (or with
-- two tokens) became two packs, whose urls collide once both are credential-free. The clean pack (else the oldest)
-- holds the source identity; the other keeps its artifacts and its own sealed source, points at the holder, and
-- leaves the unique index — so the index is recreated with that predicate. Its predicate covers a subset of the rows the old
-- index did, so recreating it cannot fail on existing data.
--
-- Additive: two nullable columns. Idempotent (IF NOT EXISTS / IF EXISTS). An older pod ignores the columns but not what
-- the new code writes into the rows: until the rollout completes it cannot Sync a sealed private pack (it clones
-- pack.url, which no longer carries the token), and its import-url fails ("more than one element") for a repository whose
-- legacy fork has become a holder plus a duplicate. In an Api/Worker split, roll the Api pods out before the Worker pods,
-- which run the backfill.

ALTER TABLE pack ADD COLUMN IF NOT EXISTS encrypted_clone_url TEXT NULL;

ALTER TABLE pack ADD COLUMN IF NOT EXISTS duplicate_of_pack_id UUID NULL REFERENCES pack(id);

DROP INDEX IF EXISTS uq_pack_team_source;

CREATE UNIQUE INDEX IF NOT EXISTS uq_pack_team_source
    ON pack(team_id, url, COALESCE(subpath, '')) WHERE deleted_date IS NULL AND url IS NOT NULL AND duplicate_of_pack_id IS NULL;

COMMENT ON COLUMN pack.encrypted_clone_url IS
    'The URL the pack''s last successful import cloned, sealed with IPayloadEncryptor (purpose CodeSpace.Credentials.v1); '
    'set only when that URL carried userinfo. pack.url is the same URL without it. Never returned by any API.';

COMMENT ON COLUMN pack.duplicate_of_pack_id IS
    'The pack holding this pack''s source identity when a legacy import forked one repository into two packs. '
    'A duplicate keeps syncing from its own sealed source and is excluded from uq_pack_team_source.';
