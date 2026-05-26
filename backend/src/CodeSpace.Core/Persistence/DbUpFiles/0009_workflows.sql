-- 0009_workflows.sql
-- Workflows platform — a Dify-style node graph engine. Five tables form the substrate:
--
--   workflow            one row per saved workflow (the design-time artifact)
--   workflow_version    immutable snapshot of a definition + JSON; the engine always
--                       runs against a frozen version so editing a workflow doesn't
--                       retroactively change what live runs were executing
--   workflow_trigger    "this workflow fires when …" — type_key + config_jsonb decoded
--                       by the matcher registry
--   workflow_run        one row per (manual or trigger-fired) execution
--   workflow_run_node   one row per (run × executed node × iteration). Iteration_key
--                       is non-null only for iterator nodes — Phase 1 leaves it as a
--                       column reservation so the engine schema doesn't have to grow
--                       when iterators land
--
-- Definitions live as JSON in workflow_version.definition_jsonb. The shape (schemaVersion,
-- nodes[], edges[]) is validated by DefinitionValidator at write time; nothing in this
-- schema enforces it because Postgres can't keep up with JSON-schema evolution and the
-- write path is the only mutation surface.

CREATE TABLE workflow (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id                     UUID NOT NULL REFERENCES team(id),
    name                        TEXT NOT NULL,
    description                 TEXT,

    -- The "currently live" definition. Engine reads this when a trigger fires. When the
    -- workflow is edited, we bump latest_version, copy the new JSON into
    -- workflow_version, and overwrite definition_jsonb here. Already-running runs keep
    -- pointing at their own workflow_version.version snapshot, so they see no change.
    definition_jsonb            JSONB NOT NULL,
    latest_version              INT NOT NULL DEFAULT 1,
    enabled                     BOOLEAN NOT NULL DEFAULT TRUE,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,
    deleted_date                TIMESTAMPTZ
);

CREATE INDEX idx_workflow_team_active
    ON workflow(team_id) WHERE deleted_date IS NULL;


-- Immutable history. Every save copies the new definition_jsonb here BEFORE the workflow
-- row is updated, so a run that captured workflow_version=5 can always be re-fetched
-- byte-for-byte for replay / debugging.
CREATE TABLE workflow_version (
    workflow_id                 UUID NOT NULL REFERENCES workflow(id),
    version                     INT NOT NULL,
    definition_jsonb            JSONB NOT NULL,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,

    PRIMARY KEY (workflow_id, version)
);


-- One trigger row per (workflow × subscription). A workflow can have many triggers — e.g.
-- "fire on pr.opened" + "fire on schedule daily 3am". type_key picks the matcher; the
-- matcher reads config_jsonb to decide whether a NormalizedEvent (or wall-clock tick)
-- applies to this trigger.
CREATE TABLE workflow_trigger (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_id                 UUID NOT NULL REFERENCES workflow(id),
    type_key                    TEXT NOT NULL,
    config_jsonb                JSONB NOT NULL DEFAULT '{}'::jsonb,
    enabled                     BOOLEAN NOT NULL DEFAULT TRUE,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,
    deleted_date                TIMESTAMPTZ
);

CREATE INDEX idx_workflow_trigger_active_by_type
    ON workflow_trigger(type_key) WHERE deleted_date IS NULL AND enabled = TRUE;

CREATE INDEX idx_workflow_trigger_workflow
    ON workflow_trigger(workflow_id) WHERE deleted_date IS NULL;


-- One row per execution. trigger_kind lets us tell apart "user clicked Run", a webhook-
-- delivered event, and a cron tick — useful for filtering history. trigger_payload_jsonb
-- is the payload the run started from (the NormalizedEvent JSON, the cron context, etc);
-- it's what the StartNode exposes as outputs to downstream nodes.
CREATE TABLE workflow_run (
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_id                 UUID NOT NULL REFERENCES workflow(id),
    workflow_version            INT NOT NULL,

    trigger_kind                TEXT NOT NULL CHECK (trigger_kind IN ('Manual','Event','Schedule')),
    trigger_payload_jsonb       JSONB NOT NULL DEFAULT '{}'::jsonb,

    status                      TEXT NOT NULL DEFAULT 'Pending'
                                  CHECK (status IN ('Pending','Running','Success','Failure','Cancelled')),
    error                       TEXT,
    started_at                  TIMESTAMPTZ,
    completed_at                TIMESTAMPTZ,

    -- Set when a Suspended node resumes — the engine restarts from this node id instead
    -- of from the start node. Null on first execution.
    resumed_from_node_id        TEXT,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,

    FOREIGN KEY (workflow_id, workflow_version) REFERENCES workflow_version(workflow_id, version)
);

CREATE INDEX idx_workflow_run_by_workflow_started
    ON workflow_run(workflow_id, started_at DESC);

CREATE INDEX idx_workflow_run_active
    ON workflow_run(status) WHERE status IN ('Pending','Running');


-- One row per (run × node × iteration). iteration_key is empty string for non-iterator
-- nodes; iterator nodes (future) write one row per element. PK on (run_id, node_id,
-- iteration_key) means a re-run idempotently overwrites — but we don't currently re-run
-- a node within the same run, so in Phase 1 that's just defensive.
CREATE TABLE workflow_run_node (
    run_id                      UUID NOT NULL REFERENCES workflow_run(id),
    node_id                     TEXT NOT NULL,
    iteration_key               TEXT NOT NULL DEFAULT '',

    status                      TEXT NOT NULL CHECK (status IN ('Pending','Running','Success','Failure','Skipped','Suspended')),
    inputs_jsonb                JSONB NOT NULL DEFAULT '{}'::jsonb,
    outputs_jsonb               JSONB NOT NULL DEFAULT '{}'::jsonb,
    error                       TEXT,
    started_at                  TIMESTAMPTZ,
    completed_at                TIMESTAMPTZ,

    created_date                TIMESTAMPTZ NOT NULL,
    created_by                  UUID NOT NULL,
    last_modified_date          TIMESTAMPTZ NOT NULL,
    last_modified_by            UUID NOT NULL,

    PRIMARY KEY (run_id, node_id, iteration_key)
);

CREATE INDEX idx_workflow_run_node_by_run
    ON workflow_run_node(run_id);
