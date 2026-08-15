-- 0127_artifact_cas_v2.sql
--
-- Wave 3 additive provider-neutral artifact CAS schema. Global reusable byte/storage facts use artifact_* names;
-- only workflow_run_artifact_reference is run-owned semantic lineage. This migration does NOT cut over any runtime
-- reader/writer, rename legacy artifact tables, or grant completion/delivery authority to these rows.
--
-- Object identity is immutable team + binary digest. Locations bind an exact immutable storage-profile revision;
-- every location revision requires an append-only event at transaction commit. Transfer state/revision is monotonic
-- and idempotent. Run references bind an exact object and tenant-bound run/plan identity, with one-way supersession.
--
-- Rollback: drop the five tables, artifact_cas_* functions, and the three additive alternate keys.

ALTER TABLE storage_profile_revision
    ADD CONSTRAINT ak_storage_profile_revision_team_id UNIQUE (team_id, id);

ALTER TABLE workflow_run
    ADD CONSTRAINT ak_workflow_run_team_id UNIQUE (team_id, id);

ALTER TABLE work_plan
    ADD CONSTRAINT ak_work_plan_artifact_scope UNIQUE (team_id, id, workflow_run_id, version);

CREATE TABLE artifact_object (
    id                UUID        NOT NULL PRIMARY KEY,
    team_id           UUID        NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    digest_algorithm  VARCHAR(16) NOT NULL,
    digest            BYTEA       NOT NULL,
    size_bytes        BIGINT      NOT NULL,
    created_date      TIMESTAMPTZ NOT NULL,
    created_by        UUID        NOT NULL,

    CONSTRAINT ak_artifact_object_team_id UNIQUE (team_id, id),
    CONSTRAINT ck_artifact_object_digest CHECK (digest_algorithm IN ('Sha256') AND octet_length(digest) = 32),
    CONSTRAINT ck_artifact_object_size CHECK (size_bytes >= 0)
);

CREATE UNIQUE INDEX ux_artifact_object_digest
    ON artifact_object (team_id, digest_algorithm, digest);
CREATE INDEX ix_artifact_object_team_created
    ON artifact_object (team_id, created_date, id);

CREATE TABLE artifact_location (
    id                            UUID          NOT NULL PRIMARY KEY,
    team_id                       UUID          NOT NULL,
    artifact_object_id            UUID          NOT NULL,
    storage_profile_revision_id   UUID          NOT NULL,
    locator                       VARCHAR(2048) NOT NULL,
    object_key                    VARCHAR(2048) NOT NULL,
    provider_object_version       VARCHAR(512)  NULL,
    provider_etag                 VARCHAR(512)  NULL,
    provider_checksum_algorithm   VARCHAR(64)   NULL,
    provider_checksum             BYTEA         NULL,
    observed_size_bytes           BIGINT        NULL,
    content_encoding              VARCHAR(64)   NULL,
    encryption_key_version        VARCHAR(512)  NULL,
    state                         VARCHAR(24)   NOT NULL,
    revision                      BIGINT        NOT NULL,
    verified_at                   TIMESTAMPTZ   NULL,
    last_error_code               VARCHAR(128)  NULL,
    last_error_message            VARCHAR(2048) NULL,
    created_date                  TIMESTAMPTZ   NOT NULL,
    created_by                    UUID          NOT NULL,
    last_modified_date            TIMESTAMPTZ   NOT NULL,
    last_modified_by              UUID          NOT NULL,

    CONSTRAINT ak_artifact_location_team_id UNIQUE (team_id, id),
    CONSTRAINT fk_artifact_location_object FOREIGN KEY (team_id, artifact_object_id)
        REFERENCES artifact_object (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_artifact_location_profile_revision FOREIGN KEY (team_id, storage_profile_revision_id)
        REFERENCES storage_profile_revision (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_artifact_location_checksum CHECK (
        (provider_checksum_algorithm IS NULL AND provider_checksum IS NULL)
        OR (provider_checksum_algorithm ~ '^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$'
            AND provider_checksum IS NOT NULL AND octet_length(provider_checksum) > 0)),
    CONSTRAINT ck_artifact_location_encoding CHECK (
        content_encoding IS NULL OR content_encoding ~ '^[a-z0-9][a-z0-9._+-]{0,63}$'),
    CONSTRAINT ck_artifact_location_error CHECK (
        (last_error_code IS NULL AND last_error_message IS NULL)
        OR (last_error_code IS NOT NULL AND btrim(last_error_code) <> '')),
    CONSTRAINT ck_artifact_location_identity CHECK (btrim(locator) <> '' AND btrim(object_key) <> ''),
    CONSTRAINT ck_artifact_location_observation CHECK (
        (observed_size_bytes IS NULL OR observed_size_bytes >= 0)
        AND (verified_at IS NULL OR verified_at >= created_date)
        AND (state <> 'Available' OR (verified_at IS NOT NULL AND observed_size_bytes IS NOT NULL
            AND provider_checksum_algorithm = 'Sha256' AND provider_checksum IS NOT NULL
            AND octet_length(provider_checksum) = 32 AND last_error_code IS NULL))),
    CONSTRAINT ck_artifact_location_revision CHECK (revision > 0),
    CONSTRAINT ck_artifact_location_state CHECK (
        state IN ('Pending', 'Available', 'Missing', 'Corrupt', 'Deleting', 'Deleted', 'Failed'))
);

CREATE UNIQUE INDEX ux_artifact_location_profile_object_key
    ON artifact_location (team_id, storage_profile_revision_id, object_key);
CREATE INDEX ix_artifact_location_object_state
    ON artifact_location (team_id, artifact_object_id, state);
CREATE INDEX ix_artifact_location_state_verified
    ON artifact_location (team_id, state, verified_at, id);

CREATE TABLE artifact_location_event (
    id                            UUID          NOT NULL PRIMARY KEY,
    team_id                       UUID          NOT NULL,
    artifact_location_id          UUID          NOT NULL,
    revision                      BIGINT        NOT NULL,
    event_type                    VARCHAR(24)   NOT NULL,
    state                         VARCHAR(24)   NOT NULL,
    observed_at                   TIMESTAMPTZ   NOT NULL,
    provider_object_version       VARCHAR(512)  NULL,
    provider_etag                 VARCHAR(512)  NULL,
    provider_checksum_algorithm   VARCHAR(64)   NULL,
    provider_checksum             BYTEA         NULL,
    observed_size_bytes           BIGINT        NULL,
    verified_at                   TIMESTAMPTZ   NULL,
    error_code                    VARCHAR(128)  NULL,
    error_message                 VARCHAR(2048) NULL,
    details_jsonb                 JSONB         NOT NULL DEFAULT '{}',
    created_by                    UUID          NOT NULL,

    CONSTRAINT fk_artifact_location_event_location FOREIGN KEY (team_id, artifact_location_id)
        REFERENCES artifact_location (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_artifact_location_event_checksum CHECK (
        (provider_checksum_algorithm IS NULL AND provider_checksum IS NULL)
        OR (provider_checksum_algorithm ~ '^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$'
            AND provider_checksum IS NOT NULL AND octet_length(provider_checksum) > 0)),
    CONSTRAINT ck_artifact_location_event_details CHECK (jsonb_typeof(details_jsonb) = 'object'),
    CONSTRAINT ck_artifact_location_event_error CHECK (
        (error_code IS NULL AND error_message IS NULL)
        OR (error_code IS NOT NULL AND btrim(error_code) <> '')),
    CONSTRAINT ck_artifact_location_event_revision CHECK (revision > 0 AND (observed_size_bytes IS NULL OR observed_size_bytes >= 0)),
    CONSTRAINT ck_artifact_location_event_type CHECK (event_type IN ('Created', 'Observed', 'Verified', 'StateChanged', 'Failed')),
    CONSTRAINT ck_artifact_location_event_state CHECK (
        state IN ('Pending', 'Available', 'Missing', 'Corrupt', 'Deleting', 'Deleted', 'Failed')),
    CONSTRAINT ux_artifact_location_event_revision UNIQUE (team_id, artifact_location_id, revision)
);

CREATE INDEX ix_artifact_location_event_team_observed
    ON artifact_location_event (team_id, observed_at, id);

CREATE TABLE artifact_transfer_intent (
    id                            UUID          NOT NULL PRIMARY KEY,
    team_id                       UUID          NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    storage_profile_revision_id   UUID          NOT NULL,
    idempotency_key               VARCHAR(256)  NOT NULL,
    expected_digest_algorithm     VARCHAR(16)   NOT NULL,
    expected_digest               BYTEA         NOT NULL,
    expected_size_bytes           BIGINT        NOT NULL,
    target_locator                VARCHAR(2048) NOT NULL,
    target_object_key             VARCHAR(2048) NOT NULL,
    temporary_object_key          VARCHAR(2048) NULL,
    provider_upload_id            VARCHAR(1024) NULL,
    state                         VARCHAR(24)   NOT NULL,
    revision                      BIGINT        NOT NULL,
    execution_attempt_id          UUID          NULL,
    execution_attempt_ordinal     INTEGER       NULL,
    execution_generation          INTEGER       NULL,
    worker_fence_epoch            BIGINT        NULL,
    retry_count                   INTEGER       NOT NULL,
    next_attempt_at               TIMESTAMPTZ   NULL,
    artifact_object_id            UUID          NULL,
    artifact_location_id          UUID          NULL,
    last_error_code               VARCHAR(128)  NULL,
    last_error_message            VARCHAR(2048) NULL,
    completed_at                  TIMESTAMPTZ   NULL,
    created_date                  TIMESTAMPTZ   NOT NULL,
    created_by                    UUID          NOT NULL,
    last_modified_date            TIMESTAMPTZ   NOT NULL,
    last_modified_by              UUID          NOT NULL,

    CONSTRAINT fk_artifact_transfer_profile_revision FOREIGN KEY (team_id, storage_profile_revision_id)
        REFERENCES storage_profile_revision (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_artifact_transfer_object FOREIGN KEY (team_id, artifact_object_id)
        REFERENCES artifact_object (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_artifact_transfer_location FOREIGN KEY (team_id, artifact_location_id)
        REFERENCES artifact_location (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_artifact_transfer_intent_attempt CHECK (
        (execution_attempt_id IS NULL AND execution_attempt_ordinal IS NULL
            AND execution_generation IS NULL AND worker_fence_epoch IS NULL)
        OR (execution_attempt_id IS NOT NULL AND execution_attempt_ordinal IS NOT NULL AND execution_attempt_ordinal > 0
            AND execution_generation IS NOT NULL AND execution_generation > 0
            AND worker_fence_epoch IS NOT NULL AND worker_fence_epoch > 0)),
    CONSTRAINT ck_artifact_transfer_intent_digest CHECK (
        expected_digest_algorithm IN ('Sha256') AND octet_length(expected_digest) = 32 AND expected_size_bytes >= 0),
    CONSTRAINT ck_artifact_transfer_intent_error CHECK (
        (last_error_code IS NULL AND last_error_message IS NULL)
        OR (last_error_code IS NOT NULL AND btrim(last_error_code) <> '')),
    CONSTRAINT ck_artifact_transfer_intent_identity CHECK (
        btrim(idempotency_key) <> '' AND btrim(target_locator) <> '' AND btrim(target_object_key) <> ''
        AND (temporary_object_key IS NULL OR btrim(temporary_object_key) <> '')
        AND (provider_upload_id IS NULL OR btrim(provider_upload_id) <> '')),
    CONSTRAINT ck_artifact_transfer_intent_outcome CHECK (
        (state = 'Committed' AND artifact_object_id IS NOT NULL AND artifact_location_id IS NOT NULL AND completed_at IS NOT NULL)
        OR (state IN ('Failed', 'Cancelled') AND artifact_object_id IS NULL AND artifact_location_id IS NULL AND completed_at IS NOT NULL)
        OR (state NOT IN ('Committed', 'Failed', 'Cancelled') AND artifact_object_id IS NULL AND artifact_location_id IS NULL AND completed_at IS NULL)),
    CONSTRAINT ck_artifact_transfer_intent_retry CHECK (
        retry_count >= 0 AND ((state = 'RetryScheduled' AND next_attempt_at IS NOT NULL AND last_error_code IS NOT NULL)
        OR (state <> 'RetryScheduled' AND next_attempt_at IS NULL))),
    CONSTRAINT ck_artifact_transfer_intent_revision CHECK (revision > 0 AND (completed_at IS NULL OR completed_at >= created_date)),
    CONSTRAINT ck_artifact_transfer_intent_state CHECK (
        state IN ('Intended', 'Uploading', 'Uploaded', 'Verifying', 'RetryScheduled', 'Committed', 'Failed', 'Cancelled')),
    CONSTRAINT ux_artifact_transfer_intent_idempotency UNIQUE (team_id, storage_profile_revision_id, idempotency_key)
);

CREATE INDEX ix_artifact_transfer_intent_state_next
    ON artifact_transfer_intent (team_id, state, next_attempt_at, id);
CREATE INDEX ix_artifact_transfer_intent_expected_digest
    ON artifact_transfer_intent (team_id, expected_digest_algorithm, expected_digest);

CREATE TABLE workflow_run_artifact_reference (
    id                          UUID          NOT NULL PRIMARY KEY,
    team_id                     UUID          NOT NULL,
    workflow_run_id             UUID          NOT NULL,
    node_id                     VARCHAR(256)  NULL,
    iteration_key               VARCHAR(1024) NOT NULL DEFAULT '',
    work_plan_id                UUID          NULL,
    plan_version                INTEGER       NULL,
    work_unit_id                VARCHAR(512)  NULL,
    work_unit_contract_hash     VARCHAR(128)  NULL,
    requirement_revision        BIGINT        NULL,
    execution_attempt_id        UUID          NULL,
    execution_attempt_ordinal   INTEGER       NULL,
    execution_generation        INTEGER       NULL,
    role                        VARCHAR(128)  NOT NULL,
    logical_path                VARCHAR(2048) NOT NULL,
    content_type                VARCHAR(255)  NOT NULL,
    required                    BOOLEAN       NOT NULL,
    retention                   VARCHAR(24)   NOT NULL,
    expires_at                  TIMESTAMPTZ   NULL,
    superseded_by_reference_id  UUID          NULL,
    artifact_object_id          UUID          NOT NULL,
    created_date                TIMESTAMPTZ   NOT NULL,
    created_by                  UUID          NOT NULL,

    CONSTRAINT ak_run_artifact_reference_team_id UNIQUE (team_id, id),
    CONSTRAINT fk_run_artifact_reference_run FOREIGN KEY (team_id, workflow_run_id)
        REFERENCES workflow_run (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_run_artifact_reference_object FOREIGN KEY (team_id, artifact_object_id)
        REFERENCES artifact_object (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_run_artifact_reference_plan FOREIGN KEY (team_id, work_plan_id, workflow_run_id, plan_version)
        REFERENCES work_plan (team_id, id, workflow_run_id, version) ON DELETE RESTRICT,
    CONSTRAINT fk_run_artifact_reference_superseded FOREIGN KEY (team_id, superseded_by_reference_id)
        REFERENCES workflow_run_artifact_reference (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_run_artifact_reference_attempt CHECK (
        (execution_attempt_id IS NULL AND execution_attempt_ordinal IS NULL AND execution_generation IS NULL)
        OR (execution_attempt_id IS NOT NULL AND execution_attempt_ordinal IS NOT NULL AND execution_attempt_ordinal > 0
            AND execution_generation IS NOT NULL AND execution_generation > 0)),
    CONSTRAINT ck_run_artifact_reference_content_type CHECK (content_type ~ '^[^[:space:]/]+/[^[:space:]]+$'),
    CONSTRAINT ck_run_artifact_reference_expiry CHECK (
        (expires_at IS NULL OR expires_at > created_date)
        AND (retention <> 'Ephemeral' OR expires_at IS NOT NULL)
        AND (retention <> 'Permanent' OR expires_at IS NULL)),
    CONSTRAINT ck_run_artifact_reference_path CHECK (
        btrim(logical_path) <> '' AND logical_path !~ '(^/|(^|/)\.\.(/|$)|\\)'),
    CONSTRAINT ck_run_artifact_reference_retention CHECK (
        retention IN ('Ephemeral', 'Run', 'Team', 'Compliance', 'Permanent')),
    CONSTRAINT ck_run_artifact_reference_role CHECK (role ~ '^[a-z0-9][a-z0-9._/-]{0,127}$'),
    CONSTRAINT ck_run_artifact_reference_superseded CHECK (
        superseded_by_reference_id IS NULL OR superseded_by_reference_id <> id),
    CONSTRAINT ck_run_artifact_reference_work_unit CHECK (
        (work_plan_id IS NULL AND plan_version IS NULL AND work_unit_id IS NULL
            AND work_unit_contract_hash IS NULL AND requirement_revision IS NULL)
        OR (work_plan_id IS NOT NULL AND plan_version IS NOT NULL AND plan_version > 0
            AND work_unit_id IS NOT NULL AND btrim(work_unit_id) <> ''
            AND (requirement_revision IS NULL OR requirement_revision > 0)))
);

CREATE INDEX ix_run_artifact_reference_active
    ON workflow_run_artifact_reference (team_id, workflow_run_id, role, logical_path, id)
    WHERE superseded_by_reference_id IS NULL;
CREATE INDEX ix_run_artifact_reference_object
    ON workflow_run_artifact_reference (team_id, artifact_object_id, id);
CREATE INDEX ix_run_artifact_reference_work_unit
    ON workflow_run_artifact_reference (work_plan_id, plan_version, work_unit_id, id)
    WHERE work_plan_id IS NOT NULL;
CREATE INDEX ix_run_artifact_reference_attempt
    ON workflow_run_artifact_reference (execution_attempt_id, execution_generation, id)
    WHERE execution_attempt_id IS NOT NULL;
CREATE INDEX ix_run_artifact_reference_expiry
    ON workflow_run_artifact_reference (expires_at, id)
    WHERE expires_at IS NOT NULL AND superseded_by_reference_id IS NULL;
CREATE UNIQUE INDEX ux_run_artifact_reference_attempt_path
    ON workflow_run_artifact_reference (team_id, workflow_run_id, execution_attempt_id, role, logical_path)
    WHERE execution_attempt_id IS NOT NULL;

CREATE OR REPLACE FUNCTION artifact_cas_object_reject_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'artifact_object is immutable — % rejected (id=%).', TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER artifact_object_enforce_immutability
    BEFORE UPDATE OR DELETE ON artifact_object
    FOR EACH ROW EXECUTE FUNCTION artifact_cas_object_reject_mutation();

CREATE OR REPLACE FUNCTION artifact_cas_location_guard() RETURNS trigger AS $$
DECLARE
    expected_size BIGINT;
    expected_digest BYTEA;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'artifact_location is durable identity — DELETE rejected (id=%).', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.revision <> 1 THEN
            RAISE EXCEPTION 'artifact_location first revision must be 1 (id=%, revision=%).', NEW.id, NEW.revision;
        END IF;
    ELSE
        IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
           OR NEW.artifact_object_id IS DISTINCT FROM OLD.artifact_object_id
           OR NEW.storage_profile_revision_id IS DISTINCT FROM OLD.storage_profile_revision_id
           OR NEW.locator IS DISTINCT FROM OLD.locator OR NEW.object_key IS DISTINCT FROM OLD.object_key
           OR NEW.created_date IS DISTINCT FROM OLD.created_date OR NEW.created_by IS DISTINCT FROM OLD.created_by THEN
            RAISE EXCEPTION 'artifact_location stable identity is immutable (id=%).', OLD.id;
        END IF;
        IF NEW.revision <> OLD.revision + 1 THEN
            RAISE EXCEPTION 'artifact_location revision must advance exactly once (id=%, old=%, new=%).', OLD.id, OLD.revision, NEW.revision;
        END IF;
        IF OLD.state = 'Deleted' THEN
            RAISE EXCEPTION 'artifact_location Deleted state is terminal (id=%).', OLD.id;
        END IF;
    END IF;

    IF NEW.state = 'Available' THEN
        SELECT size_bytes, digest INTO expected_size, expected_digest
        FROM artifact_object WHERE team_id = NEW.team_id AND id = NEW.artifact_object_id;
        IF NEW.observed_size_bytes IS DISTINCT FROM expected_size THEN
            RAISE EXCEPTION 'artifact_location Available size does not match artifact_object (id=%).', NEW.id;
        END IF;
        IF NEW.provider_checksum_algorithm IS DISTINCT FROM 'Sha256'
           OR NEW.provider_checksum IS DISTINCT FROM expected_digest THEN
            RAISE EXCEPTION 'artifact_location Available requires exact Sha256 matching artifact_object (id=%).', NEW.id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER artifact_location_guard_identity
    BEFORE INSERT OR UPDATE OR DELETE ON artifact_location
    FOR EACH ROW EXECUTE FUNCTION artifact_cas_location_guard();

CREATE OR REPLACE FUNCTION artifact_cas_location_event_reject_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'artifact_location_event is append-only — % rejected (id=%).', TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER artifact_location_event_enforce_append_only
    BEFORE UPDATE OR DELETE ON artifact_location_event
    FOR EACH ROW EXECUTE FUNCTION artifact_cas_location_event_reject_mutation();

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
          AND event.error_code IS NOT DISTINCT FROM NEW.last_error_code
          AND event.error_message IS NOT DISTINCT FROM NEW.last_error_message
    ) THEN
        RAISE EXCEPTION 'artifact_location revision requires matching append-only event snapshot (id=%, revision=%, state=%).',
            NEW.id, NEW.revision, NEW.state;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER artifact_location_require_event
    AFTER INSERT OR UPDATE ON artifact_location
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION artifact_cas_location_require_event();

CREATE OR REPLACE FUNCTION artifact_cas_transfer_guard() RETURNS trigger AS $$
DECLARE
    object_row artifact_object%ROWTYPE;
    location_row artifact_location%ROWTYPE;
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
        IF NEW.revision <> OLD.revision + 1 THEN
            RAISE EXCEPTION 'artifact_transfer_intent revision must advance exactly once (id=%, old=%, new=%).', OLD.id, OLD.revision, NEW.revision;
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

CREATE TRIGGER artifact_transfer_intent_guard
    BEFORE INSERT OR UPDATE OR DELETE ON artifact_transfer_intent
    FOR EACH ROW EXECUTE FUNCTION artifact_cas_transfer_guard();

CREATE OR REPLACE FUNCTION artifact_cas_run_reference_guard() RETURNS trigger AS $$
DECLARE
    target workflow_run_artifact_reference%ROWTYPE;
BEGIN
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
    WHERE team_id = NEW.team_id AND id = NEW.superseded_by_reference_id;
    IF NOT FOUND OR target.workflow_run_id <> OLD.workflow_run_id OR target.role <> OLD.role
       OR target.logical_path <> OLD.logical_path OR target.superseded_by_reference_id IS NOT NULL
       OR target.created_date < OLD.created_date THEN
        RAISE EXCEPTION 'workflow_run_artifact_reference superseding target must be a later active reference for the same run/role/path (id=%).', OLD.id;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_artifact_reference_guard
    BEFORE UPDATE OR DELETE ON workflow_run_artifact_reference
    FOR EACH ROW EXECUTE FUNCTION artifact_cas_run_reference_guard();
