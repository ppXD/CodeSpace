-- 0121_workflow_run_request_repository.sql
--
-- Every rejected delivery is already recorded. workflow_run_request carries the reason, the
-- provider's delivery id, the redacted headers and the verifier's diagnostic — and team_id, and
-- nothing else that says WHERE it belongs. So the one question an operator asks ("is anything
-- being thrown away for THIS repository") cannot be answered from these rows at all: a team with
-- forty repositories gets one undifferentiated pile, and the operator is standing on a repository
-- page.
--
-- The ingestion path has known the repository the whole time. It loads the webhook with
-- `.Include(w => w.Repository)` to reach the provider and the secret, and the dispatcher's
-- no-match audit is built from a NormalizedEvent whose RepositoryId is a required field. The
-- column is the only thing that was missing.
--
-- NULLABLE, and it will stay nullable. A rejection can precede the webhook lookup — a delivery to
-- an id that resolves to nothing has no repository to name, and a future source (an API push, a
-- queue consumer) may have none by construction. A NOT NULL here would force those callers to
-- invent an attribution, which is a worse answer than "we could not tell": the read path carries
-- the unattributed rows through and the tab says so, rather than dropping evidence of a delivery
-- that really did arrive.
--
-- Plain REFERENCES rather than ON DELETE SET NULL: repository is soft-deleted (unbind stamps
-- deleted_date and leaves the row), so the reference cannot dangle and an audit row cannot be
-- quietly detached from the repository it names.
--
-- No backfill. The rows that predate this are un-attributable after the fact — the delivery id is
-- the provider's, not ours, and nothing else on the row points at a repository. They read as
-- unattributed, which is the truth about them.
--
-- Additive + idempotent: one nullable column, one partial index.

ALTER TABLE workflow_run_request ADD COLUMN IF NOT EXISTS repository_id UUID NULL REFERENCES repository(id);

-- The only read there is: one repository's refusals, newest first, capped. PARTIAL on rejected
-- because that is the entire keyspace this index serves — every other status reaches these rows
-- through the run they produced, and including them here would index the successful majority to
-- answer a question only asked about the failures.
CREATE INDEX IF NOT EXISTS idx_wrr_repository_rejected
    ON workflow_run_request (repository_id, received_at DESC)
    WHERE status = 'Rejected';

COMMENT ON COLUMN workflow_run_request.repository_id IS
    'The repository the delivery was for, when it could be told. Set at every ingestion rejection '
    'site (the webhook is loaded with its repository) and at the dispatcher no-match site (the '
    'normalised event carries it). NULL means the rejection happened before anything resolved a '
    'repository — those rows are still shown, marked as unattributed, because a delivery that '
    'arrived and was discarded is exactly what the operator came to find.';
