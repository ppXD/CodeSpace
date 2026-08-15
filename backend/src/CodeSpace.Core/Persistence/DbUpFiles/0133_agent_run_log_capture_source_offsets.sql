-- 0133_agent_run_log_capture_source_offsets.sql
--
-- Redaction changes stored byte length, so resumable capture needs a separate raw-source cursor. Each spool claim
-- also records its raw-source base and a durable final-drain receipt. A log may span revise spools, but it can become
-- Completed only after the current spool session finalized at its committed source head.

DROP TRIGGER agent_run_log_stream_enforce_invariants ON agent_run_log_stream;
DROP TRIGGER agent_run_log_segment_enforce_append_only ON agent_run_log_segment;

ALTER TABLE agent_run_log_stream
    ADD COLUMN source_offset_bytes BIGINT NULL,
    ADD COLUMN capture_source_base_offset_bytes BIGINT NULL,
    ADD COLUMN capture_finalized_at TIMESTAMPTZ NULL;
UPDATE agent_run_log_stream SET
    source_offset_bytes = total_bytes,
    capture_source_base_offset_bytes = 0,
    capture_finalized_at = CASE WHEN state = 'Completed' THEN completed_at ELSE NULL END;
ALTER TABLE agent_run_log_stream
    ALTER COLUMN source_offset_bytes SET NOT NULL,
    ALTER COLUMN capture_source_base_offset_bytes SET NOT NULL;

ALTER TABLE agent_run_log_segment
    ADD COLUMN source_start_offset_bytes BIGINT NULL,
    ADD COLUMN source_length_bytes BIGINT NULL;
UPDATE agent_run_log_segment SET source_start_offset_bytes = start_offset_bytes, source_length_bytes = length_bytes;
ALTER TABLE agent_run_log_segment
    ALTER COLUMN source_start_offset_bytes SET NOT NULL,
    ALTER COLUMN source_length_bytes SET NOT NULL;

CREATE TABLE agent_run_log_capture_session (
    id                           UUID         NOT NULL PRIMARY KEY,
    team_id                      UUID         NOT NULL,
    agent_run_id                 UUID         NOT NULL,
    stream_id                    UUID         NOT NULL,
    capture_session_id           UUID         NOT NULL,
    initial_worker_fence_epoch   BIGINT       NOT NULL,
    current_worker_fence_epoch   BIGINT       NOT NULL,
    source_base_offset_bytes     BIGINT       NOT NULL,
    source_offset_bytes          BIGINT       NOT NULL,
    state                        VARCHAR(24)  NOT NULL,
    revision                     BIGINT       NOT NULL,
    created_at                   TIMESTAMPTZ  NOT NULL,
    last_observed_at             TIMESTAMPTZ  NOT NULL,
    finalized_at                 TIMESTAMPTZ  NULL,
    error_code                   VARCHAR(128) NULL,
    error_message                VARCHAR(2048) NULL,

    CONSTRAINT fk_agent_run_log_capture_session_stream FOREIGN KEY (team_id, stream_id, agent_run_id)
        REFERENCES agent_run_log_stream (team_id, id, agent_run_id) ON DELETE RESTRICT,
    CONSTRAINT ak_agent_run_log_capture_session_identity UNIQUE (team_id, stream_id, capture_session_id),
    CONSTRAINT ck_agent_run_log_capture_session_bounds CHECK (
        initial_worker_fence_epoch > 0 AND current_worker_fence_epoch >= initial_worker_fence_epoch
        AND source_base_offset_bytes >= 0 AND source_offset_bytes >= source_base_offset_bytes AND revision > 0),
    CONSTRAINT ck_agent_run_log_capture_session_identity CHECK (
        capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_agent_run_log_capture_session_state CHECK (
        (state = 'Open' AND finalized_at IS NULL AND error_code IS NULL AND error_message IS NULL)
        OR (state = 'Finalized' AND finalized_at IS NOT NULL AND error_code IS NULL AND error_message IS NULL)
        OR (state = 'CaptureFailed' AND finalized_at IS NOT NULL AND error_code IS NOT NULL)),
    CONSTRAINT ck_agent_run_log_capture_session_time CHECK (
        last_observed_at >= created_at AND (finalized_at IS NULL OR last_observed_at >= finalized_at))
);
CREATE INDEX ix_agent_run_log_capture_session_run_created
    ON agent_run_log_capture_session (team_id, agent_run_id, created_at, id);
CREATE INDEX ix_agent_run_log_capture_session_state_observed
    ON agent_run_log_capture_session (team_id, state, last_observed_at, id);

-- Preserve every pre-existing non-empty source identity before adding the segment FK. This is primarily a migration
-- compatibility path; no producer existed before this slice, but schema-v1 rows remain representable.
INSERT INTO agent_run_log_capture_session (
    id, team_id, agent_run_id, stream_id, capture_session_id,
    initial_worker_fence_epoch, current_worker_fence_epoch,
    source_base_offset_bytes, source_offset_bytes, state, revision,
    created_at, last_observed_at, finalized_at, error_code, error_message)
SELECT gen_random_uuid(), team_id, agent_run_id, stream_id, capture_session_id,
       min(worker_fence_epoch), max(worker_fence_epoch),
       min(source_start_offset_bytes), max(source_start_offset_bytes + source_length_bytes),
       'Finalized', 1, min(created_at), max(created_at), max(created_at), NULL, NULL
FROM agent_run_log_segment
GROUP BY team_id, agent_run_id, stream_id, capture_session_id;

-- Empty current sessions and the authoritative current state are preserved too. A current open session supersedes
-- the conservative historical Finalized backfill for the same identity.
INSERT INTO agent_run_log_capture_session (
    id, team_id, agent_run_id, stream_id, capture_session_id,
    initial_worker_fence_epoch, current_worker_fence_epoch,
    source_base_offset_bytes, source_offset_bytes, state, revision,
    created_at, last_observed_at, finalized_at, error_code, error_message)
SELECT gen_random_uuid(), team_id, agent_run_id, id, capture_session_id,
       worker_fence_epoch, worker_fence_epoch,
       capture_source_base_offset_bytes, source_offset_bytes,
       CASE WHEN state = 'Open' AND capture_finalized_at IS NULL THEN 'Open'
            WHEN capture_finalized_at IS NOT NULL THEN 'Finalized' ELSE 'CaptureFailed' END,
       1, created_at, last_modified_at,
       CASE WHEN state = 'Open' AND capture_finalized_at IS NULL THEN NULL ELSE COALESCE(capture_finalized_at, completed_at, last_modified_at) END,
       CASE WHEN state = 'Open' OR capture_finalized_at IS NOT NULL THEN NULL ELSE COALESCE(error_code, 'legacy-capture-failed') END,
       CASE WHEN state = 'Open' OR capture_finalized_at IS NOT NULL THEN NULL ELSE error_message END
FROM agent_run_log_stream
WHERE worker_fence_epoch IS NOT NULL AND capture_session_id IS NOT NULL
ON CONFLICT (team_id, stream_id, capture_session_id) DO UPDATE SET
    current_worker_fence_epoch = EXCLUDED.current_worker_fence_epoch,
    source_base_offset_bytes = EXCLUDED.source_base_offset_bytes,
    source_offset_bytes = EXCLUDED.source_offset_bytes,
    state = EXCLUDED.state,
    last_observed_at = EXCLUDED.last_observed_at,
    finalized_at = EXCLUDED.finalized_at,
    error_code = EXCLUDED.error_code,
    error_message = EXCLUDED.error_message;

ALTER TABLE agent_run_log_segment ADD CONSTRAINT fk_agent_run_log_segment_capture_session
    FOREIGN KEY (team_id, stream_id, capture_session_id)
    REFERENCES agent_run_log_capture_session (team_id, stream_id, capture_session_id) ON DELETE RESTRICT;

ALTER TABLE agent_run_log_stream
    DROP CONSTRAINT ck_agent_run_log_stream_head,
    DROP CONSTRAINT ck_agent_run_log_stream_time,
    DROP CONSTRAINT ck_agent_run_log_stream_terminal,
    ADD CONSTRAINT ck_agent_run_log_stream_head CHECK (
        revision > 0 AND segment_count >= 0 AND total_bytes >= 0 AND source_offset_bytes >= 0
        AND capture_source_base_offset_bytes >= 0 AND capture_source_base_offset_bytes <= source_offset_bytes
        AND next_segment_ordinal = segment_count + 1 AND next_offset_bytes = total_bytes AND schema_version > 0),
    ADD CONSTRAINT ck_agent_run_log_stream_time CHECK (
        last_modified_at >= created_at
        AND (capture_finalized_at IS NULL OR last_modified_at >= capture_finalized_at)
        AND (completed_at IS NULL OR last_modified_at >= completed_at)),
    ADD CONSTRAINT ck_agent_run_log_stream_terminal CHECK (
        ((state = 'Open' AND completed_at IS NULL AND error_code IS NULL)
            OR (state = 'Completed' AND completed_at IS NOT NULL AND error_code IS NULL)
            OR (state IN ('Truncated', 'Unavailable', 'Corrupt', 'CaptureFailed')
                AND completed_at IS NOT NULL AND error_code IS NOT NULL))
        AND (state <> 'Completed' OR (capture_finalized_at IS NOT NULL AND (schema_version = 1
            OR (content_digest_algorithm = 'Sha256' AND content_digest IS NOT NULL
                AND octet_length(content_digest) = 32)))));

ALTER TABLE agent_run_log_segment
    DROP CONSTRAINT ck_agent_run_log_segment_bounds,
    ADD CONSTRAINT ck_agent_run_log_segment_bounds CHECK (
        segment_ordinal > 0 AND start_offset_bytes >= 0 AND length_bytes > 0
        AND source_start_offset_bytes >= 0 AND source_length_bytes > 0
        AND worker_fence_epoch > 0 AND schema_version > 0);

CREATE OR REPLACE FUNCTION agent_run_log_stream_guard() RETURNS trigger AS $$
DECLARE
    appended agent_run_log_segment%ROWTYPE;
    current_fence BIGINT;
    is_claim BOOLEAN := FALSE;
    is_source_finalize BOOLEAN := FALSE;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'agent_run_log_stream is durable capture state — DELETE rejected (id=%).', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        SELECT fence_epoch INTO current_fence FROM agent_run
        WHERE team_id = NEW.team_id AND id = NEW.agent_run_id
        FOR SHARE;
        IF NOT FOUND OR current_fence <= 0 OR NEW.worker_fence_epoch IS DISTINCT FROM current_fence
           OR NEW.capture_session_id IS NULL OR NEW.schema_version < 2 THEN
            RAISE EXCEPTION 'agent_run_log_stream requires the current positive AgentRun fence and a capture session (run_id=%, attempted_fence=%).', NEW.agent_run_id, NEW.worker_fence_epoch;
        END IF;
        IF NEW.state <> 'Open' OR NEW.revision <> 1 OR NEW.segment_count <> 0 OR NEW.total_bytes <> 0
           OR NEW.source_offset_bytes <> 0 OR NEW.capture_source_base_offset_bytes <> 0
           OR NEW.capture_finalized_at IS NOT NULL OR NEW.next_segment_ordinal <> 1 OR NEW.next_offset_bytes <> 0
           OR NEW.completed_at IS NOT NULL OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL
           OR NEW.content_digest IS NOT NULL OR NEW.content_digest_algorithm IS NOT NULL
           OR NEW.last_modified_at < NEW.created_at THEN
            RAISE EXCEPTION 'agent_run_log_stream must start as an empty Open revision-one head (id=%).', NEW.id;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.agent_run_id IS DISTINCT FROM OLD.agent_run_id OR NEW.stream_kind IS DISTINCT FROM OLD.stream_kind
       OR NEW.content_type IS DISTINCT FROM OLD.content_type OR NEW.content_encoding IS DISTINCT FROM OLD.content_encoding
       OR NEW.capture_source IS DISTINCT FROM OLD.capture_source OR NEW.schema_version IS DISTINCT FROM OLD.schema_version
       OR NEW.retention IS DISTINCT FROM OLD.retention OR NEW.expires_at IS DISTINCT FROM OLD.expires_at
       OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'agent_run_log_stream stable identity is immutable (id=%).', OLD.id;
    END IF;
    IF OLD.state <> 'Open' THEN
        RAISE EXCEPTION 'agent_run_log_stream terminal state is immutable (id=%, state=%).', OLD.id, OLD.state;
    END IF;
    IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
        RAISE EXCEPTION 'agent_run_log_stream revision/time must advance monotonically (id=%, old_revision=%, new_revision=%).', OLD.id, OLD.revision, NEW.revision;
    END IF;

    IF NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
       OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id THEN
        SELECT fence_epoch INTO current_fence FROM agent_run
        WHERE team_id = NEW.team_id AND id = NEW.agent_run_id
        FOR SHARE;
        IF NOT FOUND OR current_fence <= 0 OR NEW.worker_fence_epoch IS DISTINCT FROM current_fence
           OR NEW.worker_fence_epoch < COALESCE(OLD.worker_fence_epoch, 0) OR NEW.capture_session_id IS NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream stale or malformed capture claim rejected (run_id=%, current=%, attempted=%).', NEW.agent_run_id, current_fence, NEW.worker_fence_epoch;
        END IF;
        IF NEW.capture_session_id IS NOT DISTINCT FROM OLD.capture_session_id THEN
            IF NEW.worker_fence_epoch <= COALESCE(OLD.worker_fence_epoch, 0)
               OR NEW.capture_source_base_offset_bytes IS DISTINCT FROM OLD.capture_source_base_offset_bytes
               OR NEW.capture_finalized_at IS DISTINCT FROM OLD.capture_finalized_at THEN
                RAISE EXCEPTION 'agent_run_log_stream same-session reclaim requires a strictly newer fence and preserves source state (id=%).', OLD.id;
            END IF;
        ELSIF OLD.capture_finalized_at IS NULL OR NEW.capture_source_base_offset_bytes <> OLD.source_offset_bytes
              OR NEW.capture_finalized_at IS NOT NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream stale or malformed capture claim rejected: next spool requires a finalized prior source and starts at its source head (id=%).', OLD.id;
        END IF;
        IF NEW.state IS DISTINCT FROM OLD.state OR NEW.segment_count IS DISTINCT FROM OLD.segment_count
           OR NEW.total_bytes IS DISTINCT FROM OLD.total_bytes OR NEW.source_offset_bytes IS DISTINCT FROM OLD.source_offset_bytes
           OR NEW.next_segment_ordinal IS DISTINCT FROM OLD.next_segment_ordinal
           OR NEW.next_offset_bytes IS DISTINCT FROM OLD.next_offset_bytes OR NEW.completed_at IS DISTINCT FROM OLD.completed_at
           OR NEW.error_code IS DISTINCT FROM OLD.error_code OR NEW.error_message IS DISTINCT FROM OLD.error_message
           OR NEW.content_digest_algorithm IS DISTINCT FROM OLD.content_digest_algorithm
           OR NEW.content_digest IS DISTINCT FROM OLD.content_digest THEN
            RAISE EXCEPTION 'agent_run_log_stream capture claim cannot mutate byte or terminal state (id=%).', OLD.id;
        END IF;
        is_claim := TRUE;
    END IF;

    IF NOT is_claim AND NEW.state = 'Open' AND OLD.capture_finalized_at IS NULL
       AND NEW.capture_finalized_at IS NOT NULL THEN
        IF NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
           OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
           OR NEW.segment_count IS DISTINCT FROM OLD.segment_count OR NEW.total_bytes IS DISTINCT FROM OLD.total_bytes
           OR NEW.source_offset_bytes IS DISTINCT FROM OLD.source_offset_bytes
           OR NEW.capture_source_base_offset_bytes IS DISTINCT FROM OLD.capture_source_base_offset_bytes
           OR NEW.next_segment_ordinal IS DISTINCT FROM OLD.next_segment_ordinal
           OR NEW.next_offset_bytes IS DISTINCT FROM OLD.next_offset_bytes OR NEW.completed_at IS NOT NULL
           OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL
           OR NEW.content_digest_algorithm IS NOT NULL OR NEW.content_digest IS NOT NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream source finalization cannot rewrite its claim or byte head (id=%).', OLD.id;
        END IF;
        is_source_finalize := TRUE;
    END IF;

    IF NOT is_claim AND NOT is_source_finalize AND NEW.state = 'Open' THEN
        IF NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
           OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
           OR OLD.capture_finalized_at IS NOT NULL OR NEW.capture_finalized_at IS NOT NULL
           OR NEW.capture_source_base_offset_bytes IS DISTINCT FROM OLD.capture_source_base_offset_bytes
           OR NEW.segment_count <> OLD.segment_count + 1 OR NEW.next_segment_ordinal <> OLD.next_segment_ordinal + 1
           OR NEW.total_bytes <= OLD.total_bytes OR NEW.next_offset_bytes <> NEW.total_bytes
           OR NEW.source_offset_bytes <= OLD.source_offset_bytes
           OR NEW.completed_at IS NOT NULL OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL
           OR NEW.content_digest_algorithm IS NOT NULL OR NEW.content_digest IS NOT NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream Open updates are exact one-segment head advances (id=%).', OLD.id;
        END IF;

        SELECT * INTO appended FROM agent_run_log_segment
        WHERE team_id = NEW.team_id AND stream_id = NEW.id AND agent_run_id = NEW.agent_run_id
          AND segment_ordinal = OLD.next_segment_ordinal;
        IF NOT FOUND OR appended.start_offset_bytes <> OLD.next_offset_bytes
           OR appended.length_bytes <> NEW.total_bytes - OLD.total_bytes
           OR appended.source_start_offset_bytes <> OLD.source_offset_bytes
           OR appended.source_length_bytes <> NEW.source_offset_bytes - OLD.source_offset_bytes
           OR appended.worker_fence_epoch IS DISTINCT FROM NEW.worker_fence_epoch
           OR appended.capture_session_id IS DISTINCT FROM NEW.capture_session_id
           OR appended.created_at > NEW.last_modified_at THEN
            RAISE EXCEPTION 'agent_run_log_stream head advance requires its exact claimed append-only segment (id=%, ordinal=%).', OLD.id, OLD.next_segment_ordinal;
        END IF;
    ELSIF NOT is_claim AND NOT is_source_finalize THEN
        SELECT fence_epoch INTO current_fence FROM agent_run
        WHERE team_id = NEW.team_id AND id = NEW.agent_run_id
        FOR SHARE;
        IF NOT FOUND OR current_fence <= 0 OR OLD.worker_fence_epoch IS DISTINCT FROM current_fence THEN
            RAISE EXCEPTION 'agent_run_log_stream terminal transition requires its current worker fence (run_id=%, current=%, claimed=%).', NEW.agent_run_id, current_fence, OLD.worker_fence_epoch;
        END IF;
        IF NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
           OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
           OR NEW.segment_count <> OLD.segment_count OR NEW.total_bytes <> OLD.total_bytes
           OR NEW.source_offset_bytes <> OLD.source_offset_bytes
           OR NEW.capture_source_base_offset_bytes <> OLD.capture_source_base_offset_bytes
           OR NEW.capture_finalized_at IS DISTINCT FROM OLD.capture_finalized_at
           OR NEW.next_segment_ordinal <> OLD.next_segment_ordinal OR NEW.next_offset_bytes <> OLD.next_offset_bytes
           OR NEW.completed_at IS NULL OR NEW.completed_at < OLD.created_at THEN
            RAISE EXCEPTION 'agent_run_log_stream terminal transition cannot rewrite its claim or byte head (id=%).', OLD.id;
        END IF;
        IF NEW.state = 'Completed' AND (NEW.content_digest_algorithm IS DISTINCT FROM 'Sha256'
           OR NEW.content_digest IS NULL OR octet_length(NEW.content_digest) IS DISTINCT FROM 32) THEN
            RAISE EXCEPTION 'agent_run_log_stream Completed requires its verified SHA-256 content digest (id=%).', OLD.id;
        END IF;
        IF NEW.state = 'Completed' AND OLD.capture_finalized_at IS NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream Completed requires a durable final-drain receipt (id=%).', OLD.id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION agent_run_log_segment_guard() RETURNS trigger AS $$
DECLARE
    current_fence BIGINT;
    stream agent_run_log_stream%ROWTYPE;
    object_size BIGINT;
    available_location_id UUID;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'agent_run_log_segment is append-only — % rejected (id=%).', TG_OP, OLD.id;
    END IF;

    SELECT fence_epoch INTO current_fence FROM agent_run
    WHERE team_id = NEW.team_id AND id = NEW.agent_run_id
    FOR SHARE;
    IF NOT FOUND OR NEW.worker_fence_epoch <> current_fence OR current_fence <= 0 THEN
        RAISE EXCEPTION 'agent_run_log_segment stale worker fence rejected or current fence missing (run_id=%, current=%, attempted=%).', NEW.agent_run_id, current_fence, NEW.worker_fence_epoch;
    END IF;

    SELECT * INTO stream FROM agent_run_log_stream
    WHERE team_id = NEW.team_id AND id = NEW.stream_id AND agent_run_id = NEW.agent_run_id
    FOR UPDATE;
    IF NOT FOUND OR stream.state <> 'Open' OR stream.capture_finalized_at IS NOT NULL THEN
        RAISE EXCEPTION 'agent_run_log_segment requires its open tenant-bound stream and unfinalized source (stream_id=%).', NEW.stream_id;
    END IF;
    IF NEW.worker_fence_epoch IS DISTINCT FROM stream.worker_fence_epoch
       OR NEW.capture_session_id IS DISTINCT FROM stream.capture_session_id THEN
        RAISE EXCEPTION 'agent_run_log_segment capture claim mismatch rejected (stream_id=%).', NEW.stream_id;
    END IF;
    IF NEW.segment_ordinal <> stream.next_segment_ordinal OR NEW.start_offset_bytes <> stream.next_offset_bytes
       OR NEW.source_start_offset_bytes <> stream.source_offset_bytes OR NEW.schema_version <> stream.schema_version THEN
        RAISE EXCEPTION 'agent_run_log_segment must match the locked next ordinal/offset/schema and raw-source cursor (stream_id=%).', NEW.stream_id;
    END IF;

    SELECT size_bytes INTO object_size FROM artifact_object
    WHERE team_id = NEW.team_id AND id = NEW.artifact_object_id;
    IF NOT FOUND OR object_size <> NEW.length_bytes THEN
        RAISE EXCEPTION 'agent_run_log_segment CAS object length mismatch (object_id=%, expected=%, observed=%).', NEW.artifact_object_id, NEW.length_bytes, object_size;
    END IF;
    SELECT location.id INTO available_location_id FROM artifact_location location
        WHERE location.team_id = NEW.team_id AND location.artifact_object_id = NEW.artifact_object_id
          AND location.state = 'Available' AND location.verified_at IS NOT NULL
          AND location.observed_size_bytes = NEW.length_bytes
        ORDER BY location.id LIMIT 1 FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'agent_run_log_segment CAS bytes are not verified Available (object_id=%).', NEW.artifact_object_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION agent_run_log_segment_advance_head() RETURNS trigger AS $$
BEGIN
    UPDATE agent_run_log_stream SET
        segment_count = segment_count + 1,
        total_bytes = total_bytes + NEW.length_bytes,
        source_offset_bytes = source_offset_bytes + NEW.source_length_bytes,
        next_segment_ordinal = next_segment_ordinal + 1,
        next_offset_bytes = next_offset_bytes + NEW.length_bytes,
        revision = revision + 1,
        last_modified_at = GREATEST(last_modified_at, NEW.created_at)
    WHERE team_id = NEW.team_id AND id = NEW.stream_id AND agent_run_id = NEW.agent_run_id;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION agent_run_log_capture_session_guard() RETURNS trigger AS $$
DECLARE
    stream agent_run_log_stream%ROWTYPE;
    expected_state VARCHAR(24);
    expected_finalized_at TIMESTAMPTZ;
    expected_error_code VARCHAR(128);
    expected_error_message VARCHAR(2048);
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'agent_run_log_capture_session is append-preserved capture history — DELETE rejected (id=%).', OLD.id;
    END IF;

    SELECT * INTO stream FROM agent_run_log_stream
    WHERE team_id = NEW.team_id AND id = NEW.stream_id AND agent_run_id = NEW.agent_run_id
      AND capture_session_id = NEW.capture_session_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'agent_run_log_capture_session must be the stream current session (stream_id=%, session_id=%).', NEW.stream_id, NEW.capture_session_id;
    END IF;
    expected_state := CASE
        WHEN stream.capture_finalized_at IS NOT NULL THEN 'Finalized'
        WHEN stream.state = 'CaptureFailed' THEN 'CaptureFailed'
        ELSE 'Open'
    END;
    expected_finalized_at := CASE
        WHEN stream.capture_finalized_at IS NOT NULL THEN stream.capture_finalized_at
        WHEN stream.state = 'CaptureFailed' THEN COALESCE(stream.completed_at, stream.last_modified_at)
        ELSE NULL
    END;
    expected_error_code := CASE WHEN expected_state = 'CaptureFailed' THEN stream.error_code ELSE NULL END;
    expected_error_message := CASE WHEN expected_state = 'CaptureFailed' THEN stream.error_message ELSE NULL END;
    IF NEW.current_worker_fence_epoch IS DISTINCT FROM stream.worker_fence_epoch
       OR NEW.source_base_offset_bytes IS DISTINCT FROM stream.capture_source_base_offset_bytes
       OR NEW.source_offset_bytes IS DISTINCT FROM stream.source_offset_bytes
       OR NEW.state IS DISTINCT FROM expected_state
       OR NEW.finalized_at IS DISTINCT FROM expected_finalized_at
       OR NEW.error_code IS DISTINCT FROM expected_error_code
       OR NEW.error_message IS DISTINCT FROM expected_error_message THEN
        RAISE EXCEPTION 'agent_run_log_capture_session must project the exact current stream claim/source state (stream_id=%, session_id=%).', NEW.stream_id, NEW.capture_session_id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.initial_worker_fence_epoch IS DISTINCT FROM NEW.current_worker_fence_epoch
           OR NEW.revision <> 1 OR NEW.created_at > NEW.last_observed_at THEN
            RAISE EXCEPTION 'agent_run_log_capture_session must start at revision one under its initial fence (id=%).', NEW.id;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.agent_run_id IS DISTINCT FROM OLD.agent_run_id OR NEW.stream_id IS DISTINCT FROM OLD.stream_id
       OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
       OR NEW.initial_worker_fence_epoch IS DISTINCT FROM OLD.initial_worker_fence_epoch
       OR NEW.source_base_offset_bytes IS DISTINCT FROM OLD.source_base_offset_bytes
       OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'agent_run_log_capture_session stable identity is immutable (id=%).', OLD.id;
    END IF;
    IF NEW.revision <> OLD.revision + 1 OR NEW.current_worker_fence_epoch < OLD.current_worker_fence_epoch
       OR NEW.source_offset_bytes < OLD.source_offset_bytes OR NEW.last_observed_at < OLD.last_observed_at THEN
        RAISE EXCEPTION 'agent_run_log_capture_session progress must advance monotonically (id=%).', OLD.id;
    END IF;
    IF OLD.state = 'CaptureFailed' THEN
        RAISE EXCEPTION 'agent_run_log_capture_session failed state is terminal (id=%).', OLD.id;
    END IF;
    IF OLD.state = 'Finalized' AND (NEW.state <> 'Finalized'
       OR NEW.source_offset_bytes <> OLD.source_offset_bytes
       OR NEW.finalized_at IS DISTINCT FROM OLD.finalized_at
       OR NEW.error_code IS DISTINCT FROM OLD.error_code OR NEW.error_message IS DISTINCT FROM OLD.error_message
       OR NEW.current_worker_fence_epoch <= OLD.current_worker_fence_epoch) THEN
        RAISE EXCEPTION 'agent_run_log_capture_session finalized state only admits a newer worker reclaim (id=%).', OLD.id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION agent_run_log_stream_sync_capture_session() RETURNS trigger AS $$
DECLARE
    session_state VARCHAR(24);
    session_finalized_at TIMESTAMPTZ;
    session_error_code VARCHAR(128);
    session_error_message VARCHAR(2048);
BEGIN
    IF NEW.worker_fence_epoch IS NULL OR NEW.capture_session_id IS NULL THEN RETURN NEW; END IF;
    session_state := CASE
        WHEN NEW.capture_finalized_at IS NOT NULL THEN 'Finalized'
        WHEN NEW.state = 'CaptureFailed' THEN 'CaptureFailed'
        ELSE 'Open'
    END;
    session_finalized_at := CASE WHEN session_state = 'Open' THEN NULL ELSE COALESCE(NEW.capture_finalized_at, NEW.completed_at, NEW.last_modified_at) END;
    session_error_code := CASE WHEN session_state = 'CaptureFailed' THEN NEW.error_code ELSE NULL END;
    session_error_message := CASE WHEN session_state = 'CaptureFailed' THEN NEW.error_message ELSE NULL END;

    IF TG_OP = 'INSERT' OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id THEN
        INSERT INTO agent_run_log_capture_session (
            id, team_id, agent_run_id, stream_id, capture_session_id,
            initial_worker_fence_epoch, current_worker_fence_epoch,
            source_base_offset_bytes, source_offset_bytes, state, revision,
            created_at, last_observed_at, finalized_at, error_code, error_message)
        VALUES (gen_random_uuid(), NEW.team_id, NEW.agent_run_id, NEW.id, NEW.capture_session_id,
            NEW.worker_fence_epoch, NEW.worker_fence_epoch,
            NEW.capture_source_base_offset_bytes, NEW.source_offset_bytes, session_state, 1,
            NEW.last_modified_at, NEW.last_modified_at, session_finalized_at, session_error_code, session_error_message);
        RETURN NEW;
    END IF;

    -- Completing a finalized stream, or failing its later digest/readback, does not rewrite the already-proven source
    -- session receipt. The stream terminal state records that later verification outcome independently.
    IF NEW.state = 'Completed' OR (NEW.state = 'CaptureFailed' AND NEW.capture_finalized_at IS NOT NULL) THEN RETURN NEW; END IF;

    UPDATE agent_run_log_capture_session SET
        current_worker_fence_epoch = NEW.worker_fence_epoch,
        source_offset_bytes = NEW.source_offset_bytes,
        state = session_state,
        revision = revision + 1,
        last_observed_at = NEW.last_modified_at,
        finalized_at = session_finalized_at,
        error_code = session_error_code,
        error_message = session_error_message
    WHERE team_id = NEW.team_id AND stream_id = NEW.id AND capture_session_id = NEW.capture_session_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'agent_run_log_stream current capture session ledger row disappeared (stream_id=%, session_id=%).', NEW.id, NEW.capture_session_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER agent_run_log_stream_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON agent_run_log_stream
    FOR EACH ROW EXECUTE FUNCTION agent_run_log_stream_guard();
CREATE TRIGGER agent_run_log_segment_enforce_append_only
    BEFORE INSERT OR UPDATE OR DELETE ON agent_run_log_segment
    FOR EACH ROW EXECUTE FUNCTION agent_run_log_segment_guard();
CREATE TRIGGER agent_run_log_capture_session_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON agent_run_log_capture_session
    FOR EACH ROW EXECUTE FUNCTION agent_run_log_capture_session_guard();
CREATE TRIGGER agent_run_log_stream_sync_capture_session
    AFTER INSERT OR UPDATE ON agent_run_log_stream
    FOR EACH ROW EXECUTE FUNCTION agent_run_log_stream_sync_capture_session();

COMMENT ON COLUMN agent_run_log_stream.source_offset_bytes IS
    'Monotonic raw-source cursor; independent from redacted total_bytes and used to resume capture without gaps.';
COMMENT ON COLUMN agent_run_log_stream.capture_source_base_offset_bytes IS
    'Raw-source head at which the current durable spool session started.';
COMMENT ON COLUMN agent_run_log_stream.capture_finalized_at IS
    'Durable receipt that the current spool source reached final drain at source_offset_bytes.';
COMMENT ON COLUMN agent_run_log_segment.source_start_offset_bytes IS
    'Raw source offset consumed by this immutable redacted segment.';
COMMENT ON COLUMN agent_run_log_segment.source_length_bytes IS
    'Raw source bytes consumed by this segment; may differ from length_bytes after redaction.';
