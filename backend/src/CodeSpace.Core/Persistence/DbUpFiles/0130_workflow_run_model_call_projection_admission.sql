-- 0130_workflow_run_model_call_projection_admission.sql
--
-- Load-bearing admission hardening before the append-only interaction tape can be projected into 0124's physical
-- model-call plane. The source BIGSERIAL is ordering evidence only: PostgreSQL sequences are allocated before commit,
-- are gapful, and can become visible out of allocation order. Accordingly this migration adds NO global cursor. A
-- future bounded sweeper discovers terminal facts through the candidate index + source-id anti-join and revisits the
-- explicit late-start index; it cannot skip a delayed commit by advancing a high-water sequence.
--
-- This slice remains schema-only. It does not register a job, change the model-call hot path, switch a reader, or
-- participate in completion/terminal authority.

CREATE INDEX ix_workflow_run_record_interaction_correlation
    ON workflow_run_record (run_id, correlation_id, sequence)
    WHERE correlation_id IS NOT NULL
      AND record_type IN ('interaction.started', 'interaction.completed', 'interaction.failed', 'interaction.delta');

CREATE INDEX ix_workflow_run_record_model_call_candidates
    ON workflow_run_record (record_type, occurred_at, id)
    WHERE correlation_id IS NOT NULL
      AND record_type IN ('interaction.completed', 'interaction.failed');

ALTER TABLE workflow_run_model_call
    ADD COLUMN source_kind VARCHAR(64) NULL,
    ADD COLUMN source_correlation_id UUID NULL,
    ADD CONSTRAINT fk_workflow_run_model_call_run FOREIGN KEY (team_id, workflow_run_id)
        REFERENCES workflow_run (team_id, id) ON DELETE RESTRICT,
    ADD CONSTRAINT ck_workflow_run_model_call_source_identity CHECK (
        (source_kind IS NULL AND source_correlation_id IS NULL)
        OR (source_kind IS NOT NULL AND btrim(source_kind) <> ''
            AND source_correlation_id IS NOT NULL
            AND source_correlation_id <> '00000000-0000-0000-0000-000000000000'::uuid)
    );

CREATE UNIQUE INDEX ux_workflow_run_model_call_source_identity
    ON workflow_run_model_call (team_id, workflow_run_id, source_kind, source_correlation_id)
    WHERE source_correlation_id IS NOT NULL;

ALTER TABLE workflow_run_model_call_attempt
    ADD COLUMN source_started_record_id UUID NULL,
    ADD COLUMN source_terminal_record_id UUID NULL,
    ADD COLUMN source_evidence_revision INTEGER NOT NULL DEFAULT 0,
    ADD CONSTRAINT fk_workflow_run_model_call_attempt_source_started FOREIGN KEY (source_started_record_id)
        REFERENCES workflow_run_record (id) ON DELETE RESTRICT,
    ADD CONSTRAINT fk_workflow_run_model_call_attempt_source_terminal FOREIGN KEY (source_terminal_record_id)
        REFERENCES workflow_run_record (id) ON DELETE RESTRICT,
    ADD CONSTRAINT ck_workflow_run_model_call_attempt_source_identity CHECK (
        (source_started_record_id IS NULL AND source_terminal_record_id IS NULL AND source_evidence_revision = 0)
        OR (source_terminal_record_id IS NOT NULL AND source_evidence_revision > 0)
    );

CREATE UNIQUE INDEX ux_workflow_run_model_call_attempt_source_started
    ON workflow_run_model_call_attempt (team_id, workflow_run_id, source_started_record_id)
    WHERE source_started_record_id IS NOT NULL;

CREATE UNIQUE INDEX ux_workflow_run_model_call_attempt_source_terminal
    ON workflow_run_model_call_attempt (team_id, workflow_run_id, source_terminal_record_id)
    WHERE source_terminal_record_id IS NOT NULL;

CREATE INDEX ix_workflow_run_model_call_attempt_late_start
    ON workflow_run_model_call_attempt (workflow_run_id, model_call_id)
    WHERE source_terminal_record_id IS NOT NULL AND source_started_record_id IS NULL;

CREATE OR REPLACE FUNCTION workflow_run_model_call_source_identity_guard() RETURNS TRIGGER AS $$
BEGIN
    IF OLD.source_kind IS DISTINCT FROM NEW.source_kind
        OR OLD.source_correlation_id IS DISTINCT FROM NEW.source_correlation_id THEN
        RAISE EXCEPTION
            'workflow_run_model_call source identity is immutable (call=%, old_source=%/%, new_source=%/%)',
            OLD.id, OLD.source_kind, OLD.source_correlation_id, NEW.source_kind, NEW.source_correlation_id;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_model_call_source_identity_enforce
    BEFORE UPDATE ON workflow_run_model_call
    FOR EACH ROW EXECUTE FUNCTION workflow_run_model_call_source_identity_guard();

CREATE OR REPLACE FUNCTION workflow_run_model_call_attempt_source_guard() RETURNS TRIGGER AS $$
DECLARE
    call_source_kind        VARCHAR(64);
    call_correlation_id     UUID;
    call_node_id            VARCHAR(256);
    call_iteration_key      VARCHAR(1024);
    source_type             TEXT;
    record_correlation_id   UUID;
    source_node_id          TEXT;
    source_iteration_key    TEXT;
BEGIN
    SELECT call_row.source_kind, call_row.source_correlation_id, call_row.node_id, call_row.iteration_key
      INTO call_source_kind, call_correlation_id, call_node_id, call_iteration_key
      FROM workflow_run_model_call call_row
     WHERE call_row.id = NEW.model_call_id
       AND call_row.team_id = NEW.team_id
       AND call_row.workflow_run_id = NEW.workflow_run_id
     FOR KEY SHARE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'model-call source admission parent is missing or outside scope (attempt=%, call=%)', NEW.id, NEW.model_call_id;
    END IF;

    IF call_source_kind = 'workflow-run-record/v1' THEN
        IF NEW.source_terminal_record_id IS NULL OR NEW.source_evidence_revision <= 0 THEN
            RAISE EXCEPTION 'workflow-run-record projection requires terminal identity and positive revision (attempt=%)', NEW.id;
        END IF;

        SELECT source_row.record_type, source_row.correlation_id, source_row.node_id, source_row.iteration_key
          INTO source_type, record_correlation_id, source_node_id, source_iteration_key
          FROM workflow_run_record source_row
         WHERE source_row.id = NEW.source_terminal_record_id
           AND source_row.run_id = NEW.workflow_run_id
         FOR KEY SHARE;

        IF NOT FOUND
            OR source_type NOT IN ('interaction.completed', 'interaction.failed')
            OR record_correlation_id IS DISTINCT FROM call_correlation_id
            OR source_node_id IS DISTINCT FROM call_node_id
            OR source_iteration_key IS DISTINCT FROM call_iteration_key THEN
            RAISE EXCEPTION 'model-call terminal source does not exactly match its call scope (attempt=%, record=%)', NEW.id, NEW.source_terminal_record_id;
        END IF;

        IF NEW.source_started_record_id IS NOT NULL THEN
            SELECT source_row.record_type, source_row.correlation_id, source_row.node_id, source_row.iteration_key
              INTO source_type, record_correlation_id, source_node_id, source_iteration_key
              FROM workflow_run_record source_row
             WHERE source_row.id = NEW.source_started_record_id
               AND source_row.run_id = NEW.workflow_run_id
             FOR KEY SHARE;

            IF NOT FOUND
                OR source_type <> 'interaction.started'
                OR record_correlation_id IS DISTINCT FROM call_correlation_id
                OR source_node_id IS DISTINCT FROM call_node_id
                OR source_iteration_key IS DISTINCT FROM call_iteration_key THEN
                RAISE EXCEPTION 'model-call started source does not exactly match its call scope (attempt=%, record=%)', NEW.id, NEW.source_started_record_id;
            END IF;
        END IF;
    ELSIF NEW.source_started_record_id IS NOT NULL
        OR NEW.source_terminal_record_id IS NOT NULL
        OR NEW.source_evidence_revision <> 0 THEN
        RAISE EXCEPTION 'workflow-run-record ids are only valid for workflow-run-record/v1 calls (attempt=%, source=%)', NEW.id, call_source_kind;
    END IF;

    IF TG_OP = 'UPDATE' AND OLD.source_terminal_record_id IS NOT NULL THEN
        IF NEW.source_terminal_record_id IS DISTINCT FROM OLD.source_terminal_record_id THEN
            RAISE EXCEPTION 'model-call terminal source is immutable (attempt=%)', NEW.id;
        END IF;
        IF OLD.source_started_record_id IS NOT NULL
            AND NEW.source_started_record_id IS DISTINCT FROM OLD.source_started_record_id THEN
            RAISE EXCEPTION 'model-call started source cannot be removed or replaced (attempt=%)', NEW.id;
        END IF;
        IF NEW.source_evidence_revision <> OLD.source_evidence_revision + 1 THEN
            RAISE EXCEPTION 'model-call source revision must advance exactly once (attempt=%, old=%, new=%)', NEW.id, OLD.source_evidence_revision, NEW.source_evidence_revision;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_model_call_attempt_source_enforce
    BEFORE INSERT OR UPDATE ON workflow_run_model_call_attempt
    FOR EACH ROW EXECUTE FUNCTION workflow_run_model_call_attempt_source_guard();

COMMENT ON COLUMN workflow_run_model_call.source_correlation_id IS
    'Stable logical source identity. For workflow-run-record/v1 this is correlation_id; never a BIGSERIAL cursor.';

COMMENT ON COLUMN workflow_run_model_call_attempt.source_evidence_revision IS
    'Monotonic per-attempt projection evidence revision: 0 native, 1 first source admission, +1 per late-evidence update.';
