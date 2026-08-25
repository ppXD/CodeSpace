-- 0168_workflow_run_sensitive_record_payload.sql
-- Public lifecycle records and artifacts must remain safe for operator/UI reads even when a provider echoes a
-- resolved workflow secret. Runtime recovery still needs the exact output bytes. This immutable, encrypted sidecar
-- attaches those bytes to the exact node.completed row; only the engine's dedicated Data Protection purpose reads it.

CREATE TABLE workflow_run_sensitive_record_payload (
    record_id UUID PRIMARY KEY REFERENCES workflow_run_record(id),
    run_id UUID NOT NULL REFERENCES workflow_run(id),
    team_id UUID NOT NULL REFERENCES team(id),
    payload_kind VARCHAR(64) NOT NULL,
    ciphertext TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_workflow_run_sensitive_record_payload_kind CHECK (payload_kind = 'node.outputs.v1'),
    CONSTRAINT ck_workflow_run_sensitive_record_payload_ciphertext CHECK (length(ciphertext) > 0)
);

CREATE INDEX idx_workflow_run_sensitive_record_payload_run
    ON workflow_run_sensitive_record_payload (team_id, run_id, created_at, record_id);

CREATE OR REPLACE FUNCTION workflow_run_sensitive_record_payload_validate_insert() RETURNS trigger AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM workflow_run_record record
        JOIN workflow_run run ON run.id = record.run_id
        WHERE record.id = NEW.record_id
          AND record.run_id = NEW.run_id
          AND record.record_type = 'node.completed'
          AND run.team_id = NEW.team_id
    ) THEN
        RAISE EXCEPTION 'sensitive payload must bind the exact same-team node.completed record (record=%, run=%, team=%)', NEW.record_id, NEW.run_id, NEW.team_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_sensitive_record_payload_validate
    BEFORE INSERT ON workflow_run_sensitive_record_payload
    FOR EACH ROW EXECUTE FUNCTION workflow_run_sensitive_record_payload_validate_insert();

CREATE OR REPLACE FUNCTION workflow_run_sensitive_record_payload_reject_mutations() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'workflow_run_sensitive_record_payload is immutable — % rejected (record=%).', TG_OP, OLD.record_id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_sensitive_record_payload_immutable
    BEFORE UPDATE OR DELETE ON workflow_run_sensitive_record_payload
    FOR EACH ROW EXECUTE FUNCTION workflow_run_sensitive_record_payload_reject_mutations();

COMMENT ON TABLE workflow_run_sensitive_record_payload IS
    'Encrypted recovery-only bytes for an exact immutable public ledger row. Never returned by run-detail APIs; '
    'payload_kind is a versioned decrypt/deserialize contract and ciphertext uses a workflow-specific Data Protection purpose.';

DROP VIEW workflow_run_node;

CREATE VIEW workflow_run_node AS
WITH ranked AS (
    SELECT
        id, run_id, node_id, iteration_key, record_type, occurred_at, payload_json, sequence,
        ROW_NUMBER() OVER (
            PARTITION BY run_id, node_id, iteration_key
            ORDER BY sequence DESC
        ) AS rn_latest
    FROM workflow_run_record
    WHERE record_type LIKE 'node.%' AND node_id IS NOT NULL
),
first_started AS (
    SELECT
        run_id, node_id, iteration_key,
        MIN(occurred_at) AS first_started_at,
        (SELECT payload_json->'inputs'
         FROM workflow_run_record sub
         WHERE sub.run_id = wrr.run_id
           AND sub.node_id = wrr.node_id
           AND sub.iteration_key = wrr.iteration_key
           AND sub.record_type = 'node.started'
         ORDER BY sub.sequence ASC
         LIMIT 1) AS inputs_jsonb
    FROM workflow_run_record wrr
    WHERE record_type = 'node.started' AND node_id IS NOT NULL
    GROUP BY run_id, node_id, iteration_key
),
first_occurrence AS (
    SELECT run_id, node_id, iteration_key, MIN(occurred_at) AS first_occurred_at
    FROM workflow_run_record
    WHERE record_type LIKE 'node.%' AND node_id IS NOT NULL
    GROUP BY run_id, node_id, iteration_key
)
SELECT
    r.id AS record_id,
    r.run_id,
    r.node_id,
    r.iteration_key,
    CASE r.record_type
        WHEN 'node.started'   THEN 'Running'
        WHEN 'node.completed' THEN 'Success'
        WHEN 'node.failed'    THEN 'Failure'
        WHEN 'node.skipped'   THEN 'Skipped'
        WHEN 'node.suspended' THEN 'Suspended'
        ELSE 'Pending'
    END AS status,
    COALESCE(s.inputs_jsonb, '{}'::jsonb) AS inputs_jsonb,
    COALESCE(r.payload_json->'outputs', '{}'::jsonb) AS outputs_jsonb,
    r.payload_json->'routingHints' AS routing_hints_jsonb,
    r.payload_json->>'error' AS error,
    COALESCE(s.first_started_at, f.first_occurred_at) AS started_at,
    CASE WHEN r.record_type IN ('node.completed', 'node.failed', 'node.skipped') THEN r.occurred_at ELSE NULL END AS completed_at
FROM ranked r
LEFT JOIN first_started s
    ON r.run_id = s.run_id AND r.node_id = s.node_id AND r.iteration_key = s.iteration_key
LEFT JOIN first_occurrence f
    ON r.run_id = f.run_id AND r.node_id = f.node_id AND r.iteration_key = f.iteration_key
WHERE r.rn_latest = 1;

COMMENT ON VIEW workflow_run_node IS
    'Latest node-state projection over the append-only ledger. record_id identifies the exact backing row so engine '
    'recovery can locate a same-row encrypted sensitive-output sidecar without exposing it to ordinary readers.';
