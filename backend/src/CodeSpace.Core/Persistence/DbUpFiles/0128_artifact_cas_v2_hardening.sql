-- 0128_artifact_cas_v2_hardening.sql
--
-- Additive hardening for the artifact CAS v2 schema introduced by 0127. No 0127 object is renamed or removed:
-- event snapshots gain the two location facts they previously omitted, nullable-pair checks become fail-closed,
-- and the existing guards/index are replaced in place to close concurrency and lineage bypasses.
--
-- Location/event writes remain order-independent inside one transaction. An event may lock the current location and
-- announce only its current or immediately-next revision; deferred checks then prove both directions at commit.
-- Transfer execution identity is immutable. A worker claim is a state-neutral revision that advances its fence by
-- exactly one; a saga transition pins that fence unchanged so callers can include revision+fence in their UPDATE CAS.

ALTER TABLE artifact_location_event
    ADD COLUMN content_encoding VARCHAR(64) NULL,
    ADD COLUMN encryption_key_version VARCHAR(512) NULL;

ALTER TABLE artifact_location
    DROP CONSTRAINT ck_artifact_location_checksum,
    ADD CONSTRAINT ck_artifact_location_checksum CHECK (
        (provider_checksum_algorithm IS NULL) = (provider_checksum IS NULL)
        AND (provider_checksum_algorithm IS NULL
            OR (provider_checksum_algorithm ~ '^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$'
                AND octet_length(provider_checksum) > 0)));

ALTER TABLE artifact_location_event
    DROP CONSTRAINT ck_artifact_location_event_checksum,
    ADD CONSTRAINT ck_artifact_location_event_checksum CHECK (
        (provider_checksum_algorithm IS NULL) = (provider_checksum IS NULL)
        AND (provider_checksum_algorithm IS NULL
            OR (provider_checksum_algorithm ~ '^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$'
                AND octet_length(provider_checksum) > 0)));

DROP INDEX ux_run_artifact_reference_attempt_path;
CREATE UNIQUE INDEX ux_run_artifact_reference_attempt_path
    ON workflow_run_artifact_reference (
        team_id, workflow_run_id, execution_attempt_id, execution_generation, role, logical_path)
    WHERE execution_attempt_id IS NOT NULL AND superseded_by_reference_id IS NULL;

CREATE OR REPLACE FUNCTION artifact_cas_location_require_event() RETURNS trigger AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM artifact_location_event event
        WHERE event.team_id = NEW.team_id AND event.artifact_location_id = NEW.id
          AND event.revision = NEW.revision AND event.state = NEW.state
          AND event.provider_object_version IS NOT DISTINCT FROM NEW.provider_object_version
          AND event.provider_etag IS NOT DISTINCT FROM NEW.provider_etag
          AND event.provider_checksum_algorithm IS NOT DISTINCT FROM NEW.provider_checksum_algorithm
          AND event.provider_checksum IS NOT DISTINCT FROM NEW.provider_checksum
          AND event.observed_size_bytes IS NOT DISTINCT FROM NEW.observed_size_bytes
          AND event.verified_at IS NOT DISTINCT FROM NEW.verified_at
          AND event.content_encoding IS NOT DISTINCT FROM NEW.content_encoding
          AND event.encryption_key_version IS NOT DISTINCT FROM NEW.encryption_key_version
          AND event.error_code IS NOT DISTINCT FROM NEW.last_error_code
          AND event.error_message IS NOT DISTINCT FROM NEW.last_error_message
    ) THEN
        RAISE EXCEPTION 'artifact_location revision requires matching append-only event snapshot (id=%, revision=%, state=%).',
            NEW.id, NEW.revision, NEW.state;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER artifact_location_event_enforce_append_only ON artifact_location_event;
DROP FUNCTION artifact_cas_location_event_reject_mutation();

CREATE OR REPLACE FUNCTION artifact_cas_location_event_guard() RETURNS trigger AS $$
DECLARE
    location_revision BIGINT;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'artifact_location_event is append-only — % rejected (id=%).', TG_OP, OLD.id;
    END IF;

    SELECT revision INTO location_revision
    FROM artifact_location
    WHERE team_id = NEW.team_id AND id = NEW.artifact_location_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'artifact_location_event requires its tenant-bound location (location_id=%).', NEW.artifact_location_id;
    END IF;
    IF NEW.revision < location_revision OR NEW.revision > location_revision + 1 THEN
        RAISE EXCEPTION
            'artifact_location_event may record only the current or immediately-next locked revision (location_id=%, current=%, event=%).',
            NEW.artifact_location_id, location_revision, NEW.revision;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER artifact_location_event_guard
    BEFORE INSERT OR UPDATE OR DELETE ON artifact_location_event
    FOR EACH ROW EXECUTE FUNCTION artifact_cas_location_event_guard();

CREATE OR REPLACE FUNCTION artifact_cas_event_require_location() RETURNS trigger AS $$
DECLARE
    location artifact_location%ROWTYPE;
BEGIN
    SELECT * INTO location
    FROM artifact_location
    WHERE team_id = NEW.team_id AND id = NEW.artifact_location_id;

    IF NOT FOUND OR location.revision < NEW.revision THEN
        RAISE EXCEPTION
            'artifact_location_event must match the committed location revision (location_id=%, location_revision=%, event_revision=%).',
            NEW.artifact_location_id, location.revision, NEW.revision;
    END IF;

    IF location.revision = NEW.revision AND (
        location.state IS DISTINCT FROM NEW.state
        OR location.provider_object_version IS DISTINCT FROM NEW.provider_object_version
        OR location.provider_etag IS DISTINCT FROM NEW.provider_etag
        OR location.provider_checksum_algorithm IS DISTINCT FROM NEW.provider_checksum_algorithm
        OR location.provider_checksum IS DISTINCT FROM NEW.provider_checksum
        OR location.observed_size_bytes IS DISTINCT FROM NEW.observed_size_bytes
        OR location.verified_at IS DISTINCT FROM NEW.verified_at
        OR location.content_encoding IS DISTINCT FROM NEW.content_encoding
        OR location.encryption_key_version IS DISTINCT FROM NEW.encryption_key_version
        OR location.last_error_code IS DISTINCT FROM NEW.error_code
        OR location.last_error_message IS DISTINCT FROM NEW.error_message) THEN
        RAISE EXCEPTION
            'artifact_location_event must match the committed location snapshot (location_id=%, revision=%).',
            NEW.artifact_location_id, NEW.revision;
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER artifact_location_event_require_location
    AFTER INSERT ON artifact_location_event
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION artifact_cas_event_require_location();

CREATE OR REPLACE FUNCTION artifact_cas_transfer_guard() RETURNS trigger AS $$
DECLARE
    object_row artifact_object%ROWTYPE;
    location_row artifact_location%ROWTYPE;
    is_fence_claim BOOLEAN := FALSE;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'artifact_transfer_intent is durable saga — DELETE rejected (id=%).', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.revision <> 1 THEN
            RAISE EXCEPTION 'artifact_transfer_intent first revision must be 1 (id=%, revision=%).', NEW.id, NEW.revision;
        END IF;
        IF NEW.state <> 'Intended' THEN
            RAISE EXCEPTION 'artifact_transfer_intent first state must be Intended (id=%, state=%).', NEW.id, NEW.state;
        END IF;
    ELSE
        IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
           OR NEW.storage_profile_revision_id IS DISTINCT FROM OLD.storage_profile_revision_id
           OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
           OR NEW.expected_digest_algorithm IS DISTINCT FROM OLD.expected_digest_algorithm
           OR NEW.expected_digest IS DISTINCT FROM OLD.expected_digest
           OR NEW.expected_size_bytes IS DISTINCT FROM OLD.expected_size_bytes
           OR NEW.target_locator IS DISTINCT FROM OLD.target_locator
           OR NEW.target_object_key IS DISTINCT FROM OLD.target_object_key
           OR NEW.created_date IS DISTINCT FROM OLD.created_date OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
            RAISE EXCEPTION 'artifact_transfer_intent stable identity is immutable (id=%).', OLD.id;
        END IF;
        IF NEW.execution_attempt_id IS DISTINCT FROM OLD.execution_attempt_id
           OR NEW.execution_attempt_ordinal IS DISTINCT FROM OLD.execution_attempt_ordinal
           OR NEW.execution_generation IS DISTINCT FROM OLD.execution_generation THEN
            RAISE EXCEPTION 'artifact_transfer_intent execution identity is immutable (id=%).', OLD.id;
        END IF;
        IF NEW.revision <> OLD.revision + 1 THEN
            RAISE EXCEPTION 'artifact_transfer_intent revision must advance exactly once (id=%, old=%, new=%).', OLD.id, OLD.revision, NEW.revision;
        END IF;

        IF NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch THEN
            IF OLD.worker_fence_epoch IS NULL OR NEW.worker_fence_epoch IS NULL
               OR NEW.worker_fence_epoch <> OLD.worker_fence_epoch + 1 THEN
                RAISE EXCEPTION
                    'artifact_transfer_intent worker fence may only advance exactly once and never regress (id=%, old=%, new=%).',
                    OLD.id, OLD.worker_fence_epoch, NEW.worker_fence_epoch;
            END IF;
            is_fence_claim := TRUE;
            IF NEW.state IS DISTINCT FROM OLD.state
               OR NEW.temporary_object_key IS DISTINCT FROM OLD.temporary_object_key
               OR NEW.provider_upload_id IS DISTINCT FROM OLD.provider_upload_id
               OR NEW.retry_count IS DISTINCT FROM OLD.retry_count
               OR NEW.next_attempt_at IS DISTINCT FROM OLD.next_attempt_at
               OR NEW.artifact_object_id IS DISTINCT FROM OLD.artifact_object_id
               OR NEW.artifact_location_id IS DISTINCT FROM OLD.artifact_location_id
               OR NEW.last_error_code IS DISTINCT FROM OLD.last_error_code
               OR NEW.last_error_message IS DISTINCT FROM OLD.last_error_message
               OR NEW.completed_at IS DISTINCT FROM OLD.completed_at THEN
                RAISE EXCEPTION 'artifact_transfer_intent worker fence advance is claim-only and cannot mutate saga state (id=%).', OLD.id;
            END IF;
        END IF;

        IF NOT is_fence_claim THEN
            IF NOT (
                (OLD.state = 'Intended' AND NEW.state IN ('Uploading', 'RetryScheduled', 'Failed', 'Cancelled'))
                OR (OLD.state = 'Uploading' AND NEW.state IN ('Uploaded', 'RetryScheduled', 'Failed', 'Cancelled'))
                OR (OLD.state = 'Uploaded' AND NEW.state IN ('Verifying', 'RetryScheduled', 'Failed', 'Cancelled'))
                OR (OLD.state = 'Verifying' AND NEW.state IN ('Committed', 'RetryScheduled', 'Failed', 'Cancelled'))
                OR (OLD.state = 'RetryScheduled' AND NEW.state IN ('Uploading', 'Verifying', 'Failed', 'Cancelled'))
            ) THEN
                RAISE EXCEPTION 'artifact_transfer_intent illegal state transition (id=%, old=%, new=%).', OLD.id, OLD.state, NEW.state;
            END IF;
            IF NEW.state = 'RetryScheduled' AND NEW.retry_count <> OLD.retry_count + 1 THEN
                RAISE EXCEPTION 'artifact_transfer_intent retry scheduling must increment retry_count exactly once (id=%).', OLD.id;
            ELSIF NEW.state <> 'RetryScheduled' AND NEW.retry_count <> OLD.retry_count THEN
                RAISE EXCEPTION 'artifact_transfer_intent retry_count changes only when scheduling retry (id=%).', OLD.id;
            END IF;
        END IF;
    END IF;

    IF NEW.state = 'Committed' THEN
        SELECT * INTO object_row FROM artifact_object
        WHERE team_id = NEW.team_id AND id = NEW.artifact_object_id;
        SELECT * INTO location_row FROM artifact_location
        WHERE team_id = NEW.team_id AND id = NEW.artifact_location_id;
        IF object_row.digest_algorithm IS DISTINCT FROM NEW.expected_digest_algorithm
           OR object_row.digest IS DISTINCT FROM NEW.expected_digest
           OR object_row.size_bytes IS DISTINCT FROM NEW.expected_size_bytes THEN
            RAISE EXCEPTION 'artifact_transfer_intent committed object does not match expected content (id=%).', NEW.id;
        END IF;
        IF location_row.artifact_object_id IS DISTINCT FROM NEW.artifact_object_id
           OR location_row.storage_profile_revision_id IS DISTINCT FROM NEW.storage_profile_revision_id
           OR location_row.locator IS DISTINCT FROM NEW.target_locator
           OR location_row.object_key IS DISTINCT FROM NEW.target_object_key
           OR location_row.state <> 'Available' THEN
            RAISE EXCEPTION 'artifact_transfer_intent committed location does not match verified target (id=%).', NEW.id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION artifact_cas_run_reference_guard() RETURNS trigger AS $$
DECLARE
    target workflow_run_artifact_reference%ROWTYPE;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.superseded_by_reference_id IS NOT NULL THEN
            RAISE EXCEPTION 'workflow_run_artifact_reference must start active; supersession is an UPDATE-only transition (id=%).', NEW.id;
        END IF;
        RETURN NEW;
    END IF;

    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'workflow_run_artifact_reference is durable history — DELETE rejected (id=%).', OLD.id;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id OR NEW.node_id IS DISTINCT FROM OLD.node_id
       OR NEW.iteration_key IS DISTINCT FROM OLD.iteration_key OR NEW.work_plan_id IS DISTINCT FROM OLD.work_plan_id
       OR NEW.plan_version IS DISTINCT FROM OLD.plan_version OR NEW.work_unit_id IS DISTINCT FROM OLD.work_unit_id
       OR NEW.work_unit_contract_hash IS DISTINCT FROM OLD.work_unit_contract_hash
       OR NEW.requirement_revision IS DISTINCT FROM OLD.requirement_revision
       OR NEW.execution_attempt_id IS DISTINCT FROM OLD.execution_attempt_id
       OR NEW.execution_attempt_ordinal IS DISTINCT FROM OLD.execution_attempt_ordinal
       OR NEW.execution_generation IS DISTINCT FROM OLD.execution_generation OR NEW.role IS DISTINCT FROM OLD.role
       OR NEW.logical_path IS DISTINCT FROM OLD.logical_path OR NEW.content_type IS DISTINCT FROM OLD.content_type
       OR NEW.required IS DISTINCT FROM OLD.required OR NEW.retention IS DISTINCT FROM OLD.retention
       OR NEW.expires_at IS DISTINCT FROM OLD.expires_at OR NEW.artifact_object_id IS DISTINCT FROM OLD.artifact_object_id
       OR NEW.created_date IS DISTINCT FROM OLD.created_date OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
        RAISE EXCEPTION 'workflow_run_artifact_reference stable facts are immutable (id=%).', OLD.id;
    END IF;
    IF OLD.superseded_by_reference_id IS NOT NULL OR NEW.superseded_by_reference_id IS NULL THEN
        RAISE EXCEPTION 'workflow_run_artifact_reference supersession is one-way null to target (id=%).', OLD.id;
    END IF;

    SELECT * INTO target FROM workflow_run_artifact_reference
    WHERE team_id = NEW.team_id AND id = NEW.superseded_by_reference_id
    FOR UPDATE;
    IF NOT FOUND OR target.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id
       OR target.role IS DISTINCT FROM OLD.role OR target.logical_path IS DISTINCT FROM OLD.logical_path
       OR target.superseded_by_reference_id IS NOT NULL OR target.created_date <= OLD.created_date THEN
        RAISE EXCEPTION 'workflow_run_artifact_reference superseding target must be a strictly later active reference for the same run/role/path (id=%).', OLD.id;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER workflow_run_artifact_reference_guard ON workflow_run_artifact_reference;
CREATE TRIGGER workflow_run_artifact_reference_guard
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_artifact_reference
    FOR EACH ROW EXECUTE FUNCTION artifact_cas_run_reference_guard();
