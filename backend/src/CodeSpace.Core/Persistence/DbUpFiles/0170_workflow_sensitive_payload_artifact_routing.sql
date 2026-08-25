-- 0170_workflow_sensitive_payload_artifact_routing.sql
-- Keep hot ledger-side rows bounded: encrypted recovery bytes larger than the shared artifact inline threshold route
-- through the verified content-addressable artifact plane. The holder row records exactly one storage shape and the
-- retention oracle sees its reference, so a crash before holder admission is reclaimable while a live sidecar is safe.

ALTER TABLE workflow_run_sensitive_record_payload
    ADD COLUMN ciphertext_artifact_id UUID NULL REFERENCES workflow_artifact(id) ON DELETE RESTRICT,
    ADD COLUMN ciphertext_size_bytes BIGINT NULL;

UPDATE workflow_run_sensitive_record_payload SET ciphertext_size_bytes = octet_length(ciphertext);

ALTER TABLE workflow_run_sensitive_record_payload
    ALTER COLUMN ciphertext DROP NOT NULL,
    ALTER COLUMN ciphertext_size_bytes SET NOT NULL,
    DROP CONSTRAINT ck_workflow_run_sensitive_record_payload_ciphertext,
    ADD CONSTRAINT ck_workflow_run_sensitive_record_payload_storage CHECK (
        ciphertext_size_bytes > 0
        AND ((ciphertext IS NOT NULL AND length(ciphertext) > 0 AND ciphertext_artifact_id IS NULL)
            OR (ciphertext IS NULL AND ciphertext_artifact_id IS NOT NULL))
    );

CREATE INDEX ix_workflow_run_sensitive_record_payload_ciphertext_artifact
    ON workflow_run_sensitive_record_payload (ciphertext_artifact_id)
    WHERE ciphertext_artifact_id IS NOT NULL;

CREATE OR REPLACE FUNCTION workflow_run_sensitive_record_payload_validate_insert() RETURNS trigger AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM workflow_run_record record
        JOIN workflow_run run ON run.id = record.run_id
        WHERE record.id = NEW.record_id AND record.run_id = NEW.run_id
          AND record.record_type = 'node.completed' AND run.team_id = NEW.team_id
    ) THEN
        RAISE EXCEPTION 'sensitive payload must bind the exact same-team node.completed record (record=%, run=%, team=%)', NEW.record_id, NEW.run_id, NEW.team_id;
    END IF;

    IF NEW.ciphertext_artifact_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM workflow_artifact artifact WHERE artifact.id = NEW.ciphertext_artifact_id AND artifact.team_id = NEW.team_id
    ) THEN
        RAISE EXCEPTION 'sensitive payload artifact must belong to the exact same team (record=%, artifact=%, team=%)', NEW.record_id, NEW.ciphertext_artifact_id, NEW.team_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
