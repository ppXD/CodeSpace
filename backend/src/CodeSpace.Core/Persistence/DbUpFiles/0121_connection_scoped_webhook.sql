-- 0121_connection_scoped_webhook.sql
--
-- One hook above the repository. A GitLab group hook or a GitHub organization hook is
-- registered once and delivers events for every project underneath it, so a connection with
-- forty bound repositories stops needing forty remote hooks, forty registrations that can
-- each fail on their own, and forty rows to reconcile.
--
-- Two things this cannot reuse from repository_webhook, and why:
--
--   • The OWNER. repository_webhook is keyed on a repository, and a repository is exactly what
--     a group hook does not have. The row is keyed on the provider instance instead -- plus the
--     owner path, because "connection-wide" is the operator's word, not the provider's: GitLab
--     registers a hook on ONE group and GitHub on ONE organization, and a connection to
--     gitlab.com can hold repositories in several. One row per (connection, owner) is the
--     honest grain; a single row per connection would silently cover only the first group.
--
--   • IDENTIFICATION. repository_webhook's id in the callback URL IS the repository -- ingestion
--     joins the row to its repository and never reads the body to find out where the delivery
--     came from. A group hook delivers many repositories to one URL, so identity has to come out
--     of the payload (IWebhookRepositoryIdentifier) and be matched against the repositories bound
--     to this provider instance. A delivery for a project we have not bound is expected traffic,
--     not a fault: the group covers everything in it, most of which is none of our business.
--
-- Everything else IS reused: the same registration_status vocabulary, the same
-- Pending -> Enqueued -> Registering -> Registered walk with the same CAS columns, and the same
-- one-row-per-failed-attempt timeline that 0120 introduced. Two state machines that mean the
-- same thing would be two things to learn and two things to get subtly different.
--
-- Additive + non-breaking: two new tables and one new column with a default that preserves
-- today's behaviour for every existing connection.

-- ── Scope: which mode this connection registers in ──────────────────────────────────────────

-- 'Repository' is the default and is what every existing row gets, so nothing already bound
-- changes shape. Stored as text (like every other enum in this schema) so a psql reader sees the
-- word rather than an ordinal, and so C# enum reordering cannot silently repoint existing rows.
ALTER TABLE provider_instance
    ADD COLUMN webhook_scope TEXT NOT NULL DEFAULT 'Repository';

COMMENT ON COLUMN provider_instance.webhook_scope IS
    'Where this connection registers its webhooks: ''Repository'' (default -- one hook per bound '
    'repository, in repository_webhook) or ''Connection'' (one group/organization hook per owner '
    'path, in connection_webhook). Binding a repository under a ''Connection''-scoped instance '
    'registers no per-repository hook; the two modes never both deliver.';

-- ── The hook itself ─────────────────────────────────────────────────────────────────────────

CREATE TABLE connection_webhook (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- ON DELETE CASCADE: a connection's hooks are part of the connection, and deleting a
    -- provider instance already tears down the repositories bound through it.
    provider_instance_id  UUID NOT NULL REFERENCES provider_instance(id) ON DELETE CASCADE,

    -- The GitLab group full path ('acme/platform') or the GitHub organization login ('acme') the
    -- hook is registered ON. Taken from the bound repository's namespace_path, which is exactly
    -- that value for both providers -- so the hook always sits on the group that directly
    -- contains the project, never on a guessed ancestor the credential may not administer.
    owner_path            TEXT NOT NULL,

    -- Which credential registered it. repository_webhook infers this from its repository; a
    -- connection hook has no repository to infer from, and picking one at delete time could pick a
    -- different identity than the one that created the hook -- so the choice is recorded once. FK
    -- restricts rather than cascades: a credential cannot be deleted out from under a live hook
    -- without the teardown that removes the hook first.
    credential_id         UUID NOT NULL REFERENCES credential(id),

    -- Provider-assigned hook id. NULL until registration_status reaches 'Registered', written
    -- atomically with that transition -- same contract as repository_webhook.external_id.
    external_id           TEXT NULL,

    -- Its OWN callback URL and its OWN secret. Deliberately not shared with any repository hook:
    -- the secret this row verifies against is the one registered on the group, and rotating or
    -- revoking one mode must not touch the other.
    callback_url          TEXT NOT NULL,
    secret_enc            TEXT NOT NULL,
    subscribed_events     TEXT[] NOT NULL DEFAULT '{}',
    active                BOOLEAN NOT NULL DEFAULT TRUE,
    last_received_date    TIMESTAMPTZ NULL,

    -- The same lifecycle as repository_webhook, column for column. See
    -- RepositoryWebhookRegistrationStatus: Pending -> Enqueued -> Registering -> Registered, with
    -- Failed/DeadLettered/Cancelled as the exits. Reusing the vocabulary means an operator who has
    -- read one of these tables can read the other.
    registration_status   TEXT NOT NULL DEFAULT 'Pending',
    attempts              INT NOT NULL DEFAULT 0,
    next_attempt_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error            TEXT NULL,
    enqueued_at           TIMESTAMPTZ NULL,
    registering_at        TIMESTAMPTZ NULL,
    registered_at         TIMESTAMPTZ NULL,

    created_date          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by            UUID NOT NULL,
    last_modified_date    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_modified_by      UUID NOT NULL
);

-- One in-service hook per (connection, owner). Partial on Cancelled alone, matching
-- WebhookRegistrationLifecycle.InService -- these two MUST name the same set, because the index is
-- the only thing that can still refuse a duplicate after two racing binds both get past the read.
--
-- DeadLettered is deliberately NOT excluded here, unlike repository_webhook's 0020 index. A
-- DeadLettered row can name a hook that EXISTS at the provider and is still firing (a teardown that
-- could not delete it, or a registration an operator finished by hand). Letting a second row be
-- inserted beside it is what puts two live hooks on one group, so every push arrives twice and
-- starts two runs. A bind revives the row it already has instead of inserting another.
CREATE UNIQUE INDEX uq_connection_webhook_owner
    ON connection_webhook(provider_instance_id, owner_path)
    WHERE registration_status <> 'Cancelled';

-- The registrar/reconciler read: rows in a given state that are due.
CREATE INDEX idx_connection_webhook_due
    ON connection_webhook(registration_status, next_attempt_at);

COMMENT ON TABLE connection_webhook IS
    'One group (GitLab) or organization (GitHub) webhook, registered above the repository so a '
    'connection needs one remote hook instead of one per bound repository. Sibling of '
    'repository_webhook: same registration_status vocabulary, same CAS columns, same attempt '
    'timeline -- keyed on the provider instance and the owner path instead of a repository, '
    'because a group hook has no single repository to be keyed on.';

COMMENT ON COLUMN connection_webhook.owner_path IS
    'GitLab group full path or GitHub organization login the hook is registered on -- the bound '
    'repository''s namespace_path. One row per owner because a connection can span several groups '
    'and a provider hook only ever covers one.';

COMMENT ON COLUMN connection_webhook.secret_enc IS
    'This hook''s own encrypted secret. Inbound signature verification for a group delivery uses '
    'THIS value, never a repository hook''s -- the repository is resolved from the payload only '
    'after the signature has already passed.';

-- ── The attempt timeline ────────────────────────────────────────────────────────────────────

-- Deliberately a second table rather than a nullable second FK on repository_webhook_attempt:
-- two nullable parents plus a check constraint would make every read state which parent it means,
-- and the two lifecycles have no reason to share a row space. Column-for-column identical to
-- repository_webhook_attempt so the operator-facing diagnostics (status, response body, the
-- already-masked request) read the same in both modes -- which is what makes a GitLab Premium
-- refusal legible: the 403 and the group's own answer land in the same columns as any other
-- refusal, and the same reader shows them.
CREATE TABLE connection_webhook_attempt (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    connection_webhook_id UUID NOT NULL REFERENCES connection_webhook(id) ON DELETE CASCADE,

    attempt_number        INT NOT NULL,
    attempted_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    error                 TEXT NOT NULL,

    -- NULL means the call never reached HTTP -- a timeout, DNS, a refused connection. The same
    -- distinction 0120 exists for: "the token cannot create group hooks" vs "the instance was down".
    status_code           INT NULL,
    response_body         TEXT NULL,

    -- Secrets are masked BEFORE the row is written (ProviderCallCapture), so a database dump
    -- cannot hand its reader a working token or the hook secret.
    request_method        TEXT NULL,
    request_url           TEXT NULL,
    request_body          TEXT NULL,
    request_headers_json  JSONB NULL
);

CREATE INDEX idx_connection_webhook_attempt_hook
    ON connection_webhook_attempt(connection_webhook_id, attempt_number);

COMMENT ON TABLE connection_webhook_attempt IS
    'One row per FAILED group/organization hook registration attempt -- the connection-scoped twin '
    'of repository_webhook_attempt, carrying the provider''s own answer. This is where a GitLab '
    'Free instance''s refusal of the group-hooks endpoint is recorded verbatim, so the operator '
    'reads GitLab saying it rather than a paraphrase.';
