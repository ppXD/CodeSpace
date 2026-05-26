-- 0014_activation_and_request.sql
-- Phase 2.9 — generalise the run-source model. Two distinct moves rolled into one
-- migration because they're coupled at the entity-rename level:
--
--   1. RENAME workflow_trigger → workflow_activation.
--      The table shape stays identical (id, workflow_id, type_key, config_jsonb,
--      enabled, audit columns + soft-delete) — only the name changes. The old name
--      "trigger" suggested "asynchronous webhook event", but the concept covers any
--      run source: manual UI clicks, scheduled cron firings, API invocations, replay
--      requests, child-workflow calls, provider events. "Activation" is the broader
--      noun.
--
--   2. NEW workflow_run_request table. Every run now flows through a request record
--      that captures the source (manual / api / schedule / replay / provider.*) and
--      its raw + normalised payload + idempotency + actor / correlation metadata.
--      The mutable trigger_kind enum on workflow_run goes away; source identity lives
--      on the request, and workflow_run carries only an FK back.
--
-- Greenfield ops: no data to preserve. The migration drops trigger_kind +
-- trigger_payload_jsonb on workflow_run wholesale; the integration test fixture
-- recreates its DB on first use.

-- ─── 1. Rename workflow_trigger → workflow_activation ─────────────────────────

ALTER TABLE workflow_trigger RENAME TO workflow_activation;
ALTER INDEX idx_workflow_trigger_active_by_type RENAME TO idx_workflow_activation_active_by_type;
ALTER INDEX idx_workflow_trigger_workflow RENAME TO idx_workflow_activation_workflow;

COMMENT ON TABLE workflow_activation IS
    'Per-workflow source configuration. type_key discriminates the source (e.g. '
    '"provider.github.pull_request", "schedule.cron", "manual"). config_jsonb holds '
    'source-specific filter / parameters. Activations are matched against incoming '
    'WorkflowRunRequest rows to decide which workflow_run to spawn.';

-- ─── 2. workflow_run_request — generic source-of-run record ───────────────────
--
-- Every workflow_run has exactly ONE request upstream. A request may spawn 0 runs
-- (validation failure, no matching activation, dedup hit) or 1 run; the engine never
-- fans out multiple runs from a single request (avoid replay ambiguity).
--
-- source_type is TEXT not enum: new sources (provider.bitbucket, mq.kafka, chat.slack)
-- add zero schema churn. The values themselves are namespaced by dot for grep-ability.
--
-- Idempotency: external sources (webhooks) supply external_event_id; idempotency_key
-- is derived from (source_type + external_event_id) at insert time. Unique-when-not-null
-- index makes duplicate deliveries a constraint violation at the DB layer.

CREATE TABLE workflow_run_request (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                     UUID NOT NULL REFERENCES team(id),

    -- Match outcome — both nullable until match is attempted.
    workflow_id                 UUID NULL REFERENCES workflow(id),
    activation_id               UUID NULL REFERENCES workflow_activation(id),
    activation_snapshot_json    JSONB NULL,                    -- frozen at match time

    -- Source identity
    source_type                 TEXT NOT NULL,                 -- 'manual' | 'api' | 'schedule.cron' | 'replay' | 'workflow.child' | 'provider.github.pull_request' | ...
    source_instance_id          TEXT NULL,                     -- e.g. "github.com/octocat/repo" or schedule id
    external_event_id           TEXT NULL,                     -- webhook delivery id, schedule fire id, etc.

    -- Idempotency + tracing
    idempotency_key             TEXT NULL,
    correlation_id              UUID NULL,                     -- caller-supplied trace id
    causation_id                UUID NULL,                     -- "this request was caused by which other request id" (replay chains)

    -- Actor
    actor_type                  TEXT NULL,                     -- 'user' | 'system' | 'webhook' | 'cron' | 'child_workflow'
    actor_id                    UUID NULL,                     -- user id when actor_type='user', etc.

    -- Lifecycle timestamps
    received_at                 TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    verified_at                 TIMESTAMPTZ NULL,              -- signature / auth verified
    normalized_at               TIMESTAMPTZ NULL,              -- raw → normalized_payload_json done

    -- Payload
    raw_headers_redacted_json   JSONB NULL,                    -- request headers with secret/auth stripped
    normalized_payload_json     JSONB NOT NULL DEFAULT '{}'::jsonb,
    request_metadata_json       JSONB NOT NULL DEFAULT '{}'::jsonb,  -- IP, user agent, etc.
    verification_result_json    JSONB NULL,                    -- signature check details

    -- State machine
    status                      TEXT NOT NULL,                 -- 'received' | 'verified' | 'normalized' | 'matched' | 'rejected' | 'consumed'
    error                       TEXT NULL                      -- populated when status='rejected'
);

-- Idempotency: dedupes duplicate webhook deliveries + duplicate replay clicks.
-- Partial unique so manual/api requests (which typically don't set idempotency_key)
-- don't collide.
CREATE UNIQUE INDEX uq_wrr_idempotency_key
    ON workflow_run_request(idempotency_key)
    WHERE idempotency_key IS NOT NULL;

-- Provider event dedup: when a webhook delivers (delivery_id, source_type), the pair
-- is unique. External_event_id alone isn't unique across sources (GitHub + GitLab
-- might both use auto-incrementing ints).
CREATE UNIQUE INDEX uq_wrr_external_event
    ON workflow_run_request(source_type, external_event_id)
    WHERE external_event_id IS NOT NULL;

-- Tenant + recency: drives the team's run-request audit view.
CREATE INDEX idx_wrr_team_received
    ON workflow_run_request(team_id, received_at DESC);

-- Match lookup: when the engine queues a run, it traces back through the request id.
CREATE INDEX idx_wrr_workflow_received
    ON workflow_run_request(workflow_id, received_at DESC)
    WHERE workflow_id IS NOT NULL;

-- Causation chains: "show me every replay of this original request".
CREATE INDEX idx_wrr_causation
    ON workflow_run_request(causation_id)
    WHERE causation_id IS NOT NULL;

COMMENT ON TABLE workflow_run_request IS
    'Phase 2.9 — generic per-source run-request record. Replaces the hardcoded trigger '
    'concept; every workflow_run has exactly one upstream request describing where the '
    'run came from. source_type is string for forward-compat (new providers / event '
    'shapes add zero schema churn). Idempotency on (source_type, external_event_id) '
    'rejects duplicate webhook deliveries at the DB layer.';

-- ─── 3. workflow_run — point back to request, drop trigger fields ─────────────

ALTER TABLE workflow_run ADD COLUMN run_request_id UUID NULL REFERENCES workflow_run_request(id);

CREATE INDEX idx_workflow_run_request ON workflow_run(run_request_id) WHERE run_request_id IS NOT NULL;

-- Trigger fields move onto the request. Greenfield drop — no production data.
ALTER TABLE workflow_run DROP CONSTRAINT workflow_run_trigger_kind_check;
ALTER TABLE workflow_run DROP COLUMN trigger_kind;
ALTER TABLE workflow_run DROP COLUMN trigger_payload_jsonb;     -- the real column name; trigger_payload_json (without _jsonb) never existed

COMMENT ON COLUMN workflow_run.run_request_id IS
    'FK to the workflow_run_request that produced this run. Run-detail UI traces back '
    'through this to render the source / actor / raw payload. Nullable until backfill, '
    'NOT NULL once the activation pipeline is fully cut over (Phase 2.9 cleanup).';
