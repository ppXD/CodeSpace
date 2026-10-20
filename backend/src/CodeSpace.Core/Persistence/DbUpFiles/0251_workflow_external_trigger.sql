-- 0251_workflow_external_trigger.sql
-- External-trigger credentials live outside workflow_activation.config_jsonb because they have their own lifecycle and a
-- gated reveal must not expose a bearer key whenever any workflow definition is read. node_id is stable across saves while
-- ReplaceActivationsAsync soft-deletes and recreates every activation row, so activation_id would become stale. enabled is
-- intentionally absent: the activation already owns the switch, and a duplicate would create conflicting truth.

CREATE TABLE workflow_external_trigger (
    id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id            UUID NOT NULL REFERENCES team(id),
    workflow_id        UUID NOT NULL REFERENCES workflow(id),
    node_id            TEXT NOT NULL,
    slug               TEXT NOT NULL,
    key_digest         TEXT NOT NULL,
    key_ciphertext     TEXT NOT NULL,
    created_date       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by         UUID NOT NULL,
    last_modified_date TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_modified_by   UUID NOT NULL,
    deleted_date       TIMESTAMPTZ NULL
);
CREATE UNIQUE INDEX uq_wet_slug ON workflow_external_trigger (slug) WHERE deleted_date IS NULL;
CREATE INDEX idx_wet_key_digest ON workflow_external_trigger (key_digest);
CREATE INDEX idx_wet_workflow_node ON workflow_external_trigger (workflow_id, node_id);
CREATE INDEX idx_wet_team ON workflow_external_trigger (team_id);
COMMENT ON TABLE workflow_external_trigger IS
    'Per-node credential and routing identity for a trigger.external node. key_digest verifies a presented key; key_ciphertext is the AES-GCM sealed form a workflows.write-gated reveal opens.';
