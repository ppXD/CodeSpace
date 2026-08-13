-- 0120_repository_webhook_attempt.sql
--
-- Until now the only thing a failed webhook registration left behind was
-- repository_webhook.last_error — one line of `ex.Message`, overwritten by the next
-- attempt. An operator staring at a DeadLettered hook could see THAT it failed ten times
-- and WHAT the tenth attempt said, and nothing else. That is not enough to act on: the
-- remedy for "403 ten times" (the token cannot create hooks — re-link it) and the remedy
-- for "nine timeouts then a 403" (the instance was unreachable AND the token is wrong) are
-- different, and last_error cannot tell them apart because it only ever holds the last one.
--
-- A TABLE rather than a JSON array column on repository_webhook:
--   • Every attempt has to survive, and the registrar's failure path writes through
--     ExecuteUpdate (a WHERE-guarded CAS, no change tracker). Appending to a JSON array
--     there would mean read-modify-write of the same row the CAS is racing on — two
--     workers reviving the same webhook would silently drop one attempt's evidence, which
--     is precisely the evidence we added this for.
--   • An INSERT of a child row is append-only by construction: concurrent attempts each
--     get their own row and nothing is lost.
--   • The timeline is what the operator reads, and a timeline wants rows — ordering,
--     filtering by status, "how many 403s" are all index scans instead of JSON traversal.
-- The cost is one join for a reader; the state machine on repository_webhook is untouched
-- (registration_status / attempts / next_attempt_at / last_error all keep their meaning,
-- and last_error still mirrors the newest attempt's error for anything already reading it).
--
-- Additive + non-breaking: a brand-new table, nothing else touched.

CREATE TABLE repository_webhook_attempt (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- ON DELETE CASCADE because the attempt log is diagnostics FOR this row, not an
    -- independent audit trail: unbind hard-deletes Registered webhook rows
    -- (RepositoryBindingService.TearDownRepositoryAsync), and a restrict here would turn
    -- an unbind of a repo that once failed registration into a foreign-key error.
    repository_webhook_id UUID NOT NULL REFERENCES repository_webhook(id) ON DELETE CASCADE,

    -- Matches repository_webhook.attempts AFTER this attempt was counted, so a reader can
    -- line the timeline up against the state machine. Not unique: the registrar documents
    -- that a reconciler reviving a stuck row can over-count by one, and a unique index
    -- would answer that by throwing away the attempt's evidence.
    attempt_number        INT NOT NULL,
    attempted_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- The same string written to repository_webhook.last_error — kept here per-attempt so
    -- the timeline is readable without a join back, and so last_error stays exactly what it
    -- has always been (the newest one).
    error                 TEXT NOT NULL,

    -- NULL when the failure never reached HTTP — a DNS failure, a connect timeout, a
    -- cancelled call. That NULL is itself the diagnosis ("we never got an answer"), which
    -- is why it is a nullable column rather than a sentinel like 0.
    status_code           INT NULL,
    response_body         TEXT NULL,

    -- What we sent. Secrets are replaced with a mask BEFORE the row is written (see
    -- ProviderCallCapture) — the webhook secret and the credential in the auth header are
    -- never in these columns, so a database dump cannot hand its reader a working token.
    request_method        TEXT NULL,
    request_url           TEXT NULL,
    request_body          TEXT NULL,
    request_headers_json  JSONB NULL
);

-- The only read there is: one webhook's attempts, oldest first.
CREATE INDEX idx_repository_webhook_attempt_hook ON repository_webhook_attempt(repository_webhook_id, attempt_number);

COMMENT ON TABLE repository_webhook_attempt IS
    'One row per FAILED provider-side webhook registration attempt — the operator-facing answer to '
    '"what actually happened", which repository_webhook.last_error cannot give because it only holds '
    'the newest error. Carries the HTTP status (NULL when the call never reached HTTP), the '
    'provider''s response body, and the request we sent with every secret already masked.';

COMMENT ON COLUMN repository_webhook_attempt.status_code IS
    'HTTP status the provider answered with. NULL means no HTTP answer at all (timeout, DNS, connect '
    'refused) — the distinction that separates "the token is wrong" from "the instance was down".';

COMMENT ON COLUMN repository_webhook_attempt.request_body IS
    'The request body we sent, with secret-bearing fields masked at capture time. Never contains the '
    'webhook secret or the provider credential.';

COMMENT ON COLUMN repository_webhook_attempt.request_headers_json IS
    'Request headers we sent, with credential-carrying header values masked at capture time. Kept so '
    'the operator can see WHICH auth scheme was used (Bearer vs PRIVATE-TOKEN) without seeing the token.';
