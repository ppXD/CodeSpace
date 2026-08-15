-- 0131_artifact_transfer_fence_claim.sql
--
-- Minimal runtime-saga hardening for the additive Artifact CAS v2 transfer ledger. Execution-attempt identity
-- remains an immutable all-or-none bundle, while the worker claim fence becomes an independent monotonic fact.
-- This lets both system-owned and attempt-owned transfers be claimed before an out-of-transaction provider call.
-- A claim is still revision+1 and state-neutral; every later transition must retain the claimed fence and its xmin
-- CAS, so a resumed/stale worker cannot commit after another worker has reclaimed the intent.

ALTER TABLE artifact_transfer_intent
    ADD COLUMN worker_lease_expires_at TIMESTAMPTZ NULL,
    DROP CONSTRAINT ck_artifact_transfer_intent_attempt,
    ADD CONSTRAINT ck_artifact_transfer_intent_attempt CHECK (
        ((execution_attempt_id IS NULL AND execution_attempt_ordinal IS NULL AND execution_generation IS NULL)
            OR (execution_attempt_id IS NOT NULL AND execution_attempt_ordinal IS NOT NULL
                AND execution_attempt_ordinal > 0 AND execution_generation IS NOT NULL AND execution_generation > 0))
        AND (worker_fence_epoch IS NULL OR worker_fence_epoch > 0));

ALTER TABLE artifact_transfer_intent
    ADD CONSTRAINT ck_artifact_transfer_intent_worker_lease CHECK (
        worker_lease_expires_at IS NULL OR worker_fence_epoch IS NOT NULL);

DROP INDEX ix_artifact_transfer_intent_state_next;
CREATE INDEX ix_artifact_transfer_intent_state_next
    ON artifact_transfer_intent (team_id, state, next_attempt_at, id)
    WHERE state IN ('Intended', 'Uploading', 'Uploaded', 'Verifying', 'RetryScheduled');
CREATE INDEX ix_artifact_transfer_intent_recovery
    ON artifact_transfer_intent (team_id, state, worker_lease_expires_at, next_attempt_at, id)
    WHERE state IN ('Intended', 'Uploading', 'Uploaded', 'Verifying', 'RetryScheduled');

CREATE OR REPLACE FUNCTION artifact_cas_transfer_guard() RETURNS trigger AS $$
DECLARE
    object_row artifact_object%ROWTYPE;
    location_row artifact_location%ROWTYPE;
    is_fence_claim BOOLEAN := FALSE;
    is_lease_renewal BOOLEAN := FALSE;
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
        IF NEW.worker_fence_epoch IS NOT NULL OR NEW.worker_lease_expires_at IS NOT NULL THEN
            RAISE EXCEPTION 'artifact_transfer_intent must be inserted unclaimed (id=%).', NEW.id;
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
            IF OLD.state IN ('Committed', 'Failed', 'Cancelled') THEN
                RAISE EXCEPTION 'artifact_transfer_intent terminal rows cannot be claimed (id=%, state=%).', OLD.id, OLD.state;
            END IF;
            IF OLD.worker_lease_expires_at > clock_timestamp() THEN
                RAISE EXCEPTION 'artifact_transfer_intent live worker lease cannot be reclaimed (id=%, fence=%).', OLD.id, OLD.worker_fence_epoch;
            END IF;
            IF NEW.worker_fence_epoch IS NULL
               OR NEW.worker_fence_epoch <> COALESCE(OLD.worker_fence_epoch, 0) + 1 THEN
                RAISE EXCEPTION
                    'artifact_transfer_intent worker fence may only claim NULL to 1 or advance exactly once (id=%, old=%, new=%).',
                    OLD.id, OLD.worker_fence_epoch, NEW.worker_fence_epoch;
            END IF;
            IF NEW.worker_lease_expires_at IS NULL OR NEW.worker_lease_expires_at <= clock_timestamp() THEN
                RAISE EXCEPTION 'artifact_transfer_intent worker fence claim requires an expiring lease (id=%).', OLD.id;
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

        IF NOT is_fence_claim
           AND NEW.worker_fence_epoch IS NOT DISTINCT FROM OLD.worker_fence_epoch
           AND NEW.worker_fence_epoch IS NOT NULL
           AND NEW.worker_lease_expires_at IS DISTINCT FROM OLD.worker_lease_expires_at
           AND NEW.worker_lease_expires_at IS NOT NULL
           AND NEW.worker_lease_expires_at > OLD.worker_lease_expires_at
           AND NEW.worker_lease_expires_at > clock_timestamp()
           AND OLD.worker_lease_expires_at > clock_timestamp()
           AND NEW.state IS NOT DISTINCT FROM OLD.state
           AND NEW.temporary_object_key IS NOT DISTINCT FROM OLD.temporary_object_key
           AND NEW.provider_upload_id IS NOT DISTINCT FROM OLD.provider_upload_id
           AND NEW.retry_count IS NOT DISTINCT FROM OLD.retry_count
           AND NEW.next_attempt_at IS NOT DISTINCT FROM OLD.next_attempt_at
           AND NEW.artifact_object_id IS NOT DISTINCT FROM OLD.artifact_object_id
           AND NEW.artifact_location_id IS NOT DISTINCT FROM OLD.artifact_location_id
           AND NEW.last_error_code IS NOT DISTINCT FROM OLD.last_error_code
           AND NEW.last_error_message IS NOT DISTINCT FROM OLD.last_error_message
           AND NEW.completed_at IS NOT DISTINCT FROM OLD.completed_at THEN
            is_lease_renewal := TRUE;
        END IF;

        IF NOT is_fence_claim AND NOT is_lease_renewal THEN
            IF NEW.worker_fence_epoch IS NULL THEN
                RAISE EXCEPTION 'artifact_transfer_intent saga transition requires a current worker fence claim (id=%).', OLD.id;
            END IF;
            IF OLD.worker_lease_expires_at IS NULL OR OLD.worker_lease_expires_at <= clock_timestamp() THEN
                RAISE EXCEPTION 'artifact_transfer_intent saga transition requires an unexpired worker lease (id=%).', OLD.id;
            END IF;
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

    IF NEW.state IN ('Committed', 'Failed', 'Cancelled')
       AND NEW.worker_lease_expires_at IS NOT NULL THEN
        RAISE EXCEPTION 'artifact_transfer_intent inactive state must release its worker lease (id=%, state=%).', NEW.id, NEW.state;
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
