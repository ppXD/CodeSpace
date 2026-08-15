-- 0124_workflow_run_model_call.sql
--
-- Additive model-call data plane, schema only: workflow_run_model_call is the LOGICAL inference request bound to
-- workflow/node/work-unit/execution-attempt identity; workflow_run_model_call_attempt is each PHYSICAL provider
-- dispatch. Retries/fallbacks therefore append attempts instead of overwriting requested/effective route, request /
-- response artifacts, usage, cost or timing. No producer, reader, terminal authority or metric consumes these rows
-- in this slice.
--
-- workflow_run_id and artifact ids are deliberate soft aggregate references, matching the existing completion /
-- artifact ledgers: telemetry may outlive operational run cleanup, and artifact purge has its own integrity protocol.
-- The composite parent FK proves each attempt's denormalized team/run scope belongs to its logical call.
-- Rollback: DROP TABLE workflow_run_model_call_attempt; DROP TABLE workflow_run_model_call;

CREATE TABLE workflow_run_model_call (
    id                          UUID         NOT NULL PRIMARY KEY,
    team_id                     UUID         NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    workflow_run_id             UUID         NOT NULL,
    node_id                     VARCHAR(256) NULL,
    iteration_key               VARCHAR(1024) NOT NULL DEFAULT '',
    work_plan_id                UUID         NULL,
    plan_version                INTEGER      NULL,
    work_unit_id                VARCHAR(512) NULL,
    work_unit_contract_hash     VARCHAR(128) NULL,
    execution_attempt_id        UUID         NULL,
    execution_attempt_ordinal   INTEGER      NULL,
    execution_generation        INTEGER      NULL,
    call_ordinal                BIGINT       NOT NULL,
    purpose                     VARCHAR(128) NOT NULL,
    requested_provider          VARCHAR(100) NULL,
    requested_model             VARCHAR(500) NULL,
    requested_model_row_id      UUID         NULL,
    selection_policy            VARCHAR(256) NULL,
    request_artifact_id         UUID         NULL,
    capture_source              VARCHAR(64)  NOT NULL,
    capture_completeness        VARCHAR(20)  NOT NULL,
    schema_version              INTEGER      NOT NULL,
    created_date                TIMESTAMPTZ  NOT NULL,
    created_by                  UUID         NOT NULL,
    last_modified_date          TIMESTAMPTZ  NOT NULL,
    last_modified_by            UUID         NOT NULL,

    CONSTRAINT ak_workflow_run_model_call_scope UNIQUE (id, team_id, workflow_run_id),
    CONSTRAINT ck_workflow_run_model_call_capture_completeness CHECK (capture_completeness IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown')),
    CONSTRAINT ck_workflow_run_model_call_execution_identity CHECK (
        (execution_attempt_id IS NULL AND execution_attempt_ordinal IS NULL AND execution_generation IS NULL)
        OR (execution_attempt_id IS NOT NULL AND execution_attempt_ordinal IS NOT NULL AND execution_attempt_ordinal > 0
            AND (execution_generation IS NULL OR execution_generation > 0))
    ),
    CONSTRAINT ck_workflow_run_model_call_positive_values CHECK (call_ordinal > 0 AND schema_version > 0),
    CONSTRAINT ck_workflow_run_model_call_provenance CHECK (btrim(purpose) <> '' AND (selection_policy IS NULL OR btrim(selection_policy) <> '')),
    CONSTRAINT ck_workflow_run_model_call_work_unit_identity CHECK (
        (work_plan_id IS NULL AND plan_version IS NULL AND work_unit_id IS NULL AND work_unit_contract_hash IS NULL)
        OR (work_plan_id IS NOT NULL AND plan_version IS NOT NULL AND plan_version > 0
            AND work_unit_id IS NOT NULL AND btrim(work_unit_id) <> '')
    )
);

CREATE INDEX ix_workflow_run_model_call_run_created ON workflow_run_model_call (workflow_run_id, created_date, id);
CREATE INDEX ix_workflow_run_model_call_team_created ON workflow_run_model_call (team_id, created_date, id);
CREATE INDEX ix_workflow_run_model_call_execution_attempt ON workflow_run_model_call (execution_attempt_id, call_ordinal) WHERE execution_attempt_id IS NOT NULL;
CREATE INDEX ix_workflow_run_model_call_work_unit ON workflow_run_model_call (work_plan_id, plan_version, work_unit_id) WHERE work_plan_id IS NOT NULL;
CREATE INDEX ix_workflow_run_model_call_requested_model_row ON workflow_run_model_call (requested_model_row_id, created_date) WHERE requested_model_row_id IS NOT NULL;

CREATE TABLE workflow_run_model_call_attempt (
    id                      UUID         NOT NULL PRIMARY KEY,
    team_id                 UUID         NOT NULL,
    workflow_run_id         UUID         NOT NULL,
    model_call_id           UUID         NOT NULL,
    attempt_ordinal         INTEGER      NOT NULL,
    effective_provider      VARCHAR(100) NULL,
    effective_model         VARCHAR(500) NULL,
    effective_model_row_id  UUID         NULL,
    transport_kind          VARCHAR(64)  NULL,
    endpoint_fingerprint    VARCHAR(256) NULL,
    provider_request_id     VARCHAR(512) NULL,
    request_artifact_id     UUID         NULL,
    response_artifact_id    UUID         NULL,
    error_artifact_id       UUID         NULL,
    status                  VARCHAR(32)  NOT NULL,
    error_code              VARCHAR(200) NULL,
    finish_reason           VARCHAR(100) NULL,
    http_status_code        INTEGER      NULL,
    capture_source          VARCHAR(64)  NOT NULL,
    capture_completeness    VARCHAR(20)  NOT NULL,
    input_tokens            BIGINT       NULL,
    output_tokens           BIGINT       NULL,
    cache_read_tokens       BIGINT       NULL,
    cache_write_tokens      BIGINT       NULL,
    reasoning_tokens        BIGINT       NULL,
    cost_amount             NUMERIC(18,8) NULL,
    cost_currency           VARCHAR(3)   NULL,
    pricing_version         VARCHAR(200) NULL,
    started_at              TIMESTAMPTZ  NOT NULL,
    first_token_at          TIMESTAMPTZ  NULL,
    completed_at            TIMESTAMPTZ  NULL,
    schema_version          INTEGER      NOT NULL,
    created_date            TIMESTAMPTZ  NOT NULL,
    created_by              UUID         NOT NULL,
    last_modified_date      TIMESTAMPTZ  NOT NULL,
    last_modified_by        UUID         NOT NULL,

    CONSTRAINT fk_workflow_run_model_call_attempt_call FOREIGN KEY (model_call_id, team_id, workflow_run_id)
        REFERENCES workflow_run_model_call (id, team_id, workflow_run_id) ON DELETE CASCADE,
    CONSTRAINT ck_workflow_run_model_call_attempt_capture_completeness CHECK (capture_completeness IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown')),
    CONSTRAINT ck_workflow_run_model_call_attempt_cost CHECK (
        (cost_amount IS NULL AND cost_currency IS NULL)
        OR (cost_amount IS NOT NULL AND cost_amount >= 0 AND cost_currency IS NOT NULL AND cost_currency ~ '^[A-Z]{3}$')
    ),
    CONSTRAINT ck_workflow_run_model_call_attempt_http_status CHECK (http_status_code IS NULL OR http_status_code BETWEEN 100 AND 599),
    CONSTRAINT ck_workflow_run_model_call_attempt_positive_values CHECK (
        attempt_ordinal > 0 AND schema_version > 0
        AND (input_tokens IS NULL OR input_tokens >= 0)
        AND (output_tokens IS NULL OR output_tokens >= 0)
        AND (cache_read_tokens IS NULL OR cache_read_tokens >= 0)
        AND (cache_write_tokens IS NULL OR cache_write_tokens >= 0)
        AND (reasoning_tokens IS NULL OR reasoning_tokens >= 0)
    ),
    CONSTRAINT ck_workflow_run_model_call_attempt_status CHECK (status IN ('Pending', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'TimedOut', 'Indeterminate')),
    CONSTRAINT ck_workflow_run_model_call_attempt_timing CHECK (
        (first_token_at IS NULL OR first_token_at >= started_at)
        AND (completed_at IS NULL OR completed_at >= started_at)
        AND (first_token_at IS NULL OR completed_at IS NULL OR first_token_at <= completed_at)
    )
);

CREATE UNIQUE INDEX ux_workflow_run_model_call_attempt_ordinal ON workflow_run_model_call_attempt (model_call_id, attempt_ordinal);
CREATE INDEX ix_workflow_run_model_call_attempt_run_started ON workflow_run_model_call_attempt (workflow_run_id, started_at, id);
CREATE INDEX ix_workflow_run_model_call_attempt_team_started ON workflow_run_model_call_attempt (team_id, started_at, id);
CREATE INDEX ix_workflow_run_model_call_attempt_provider_request ON workflow_run_model_call_attempt (effective_provider, provider_request_id) WHERE provider_request_id IS NOT NULL;
CREATE INDEX ix_workflow_run_model_call_attempt_effective_model_row ON workflow_run_model_call_attempt (effective_model_row_id, started_at) WHERE effective_model_row_id IS NOT NULL;
