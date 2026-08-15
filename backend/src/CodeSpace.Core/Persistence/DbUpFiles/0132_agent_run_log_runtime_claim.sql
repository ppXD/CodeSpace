-- 0132_agent_run_log_runtime_claim.sql
--
-- Adds the capture claim and terminal content digest required by the generic Agent Run log runtime. Existing v1
-- schema-only rows remain readable; every new runtime stream is schema v2 and is fenced by the AgentRun's current
-- worker epoch plus an opaque capture-session id. A higher AgentRun fence may reclaim an Open stream, but an equal
-- or stale fence cannot replace its session. Segment admission and terminalization both prove that claim in SQL.

ALTER TABLE agent_run_log_stream
    ADD COLUMN worker_fence_epoch BIGINT NULL,
    ADD COLUMN capture_session_id UUID NULL,
    ADD COLUMN content_digest_algorithm VARCHAR(16) NULL,
    ADD COLUMN content_digest BYTEA NULL,
    ADD CONSTRAINT ck_agent_run_log_stream_claim CHECK (
        (worker_fence_epoch IS NULL AND capture_session_id IS NULL)
        OR (worker_fence_epoch IS NOT NULL AND worker_fence_epoch > 0 AND capture_session_id IS NOT NULL
            AND capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
    ADD CONSTRAINT ck_agent_run_log_stream_digest CHECK (
        (content_digest_algorithm IS NULL AND content_digest IS NULL)
        OR (content_digest_algorithm IS NOT NULL AND content_digest_algorithm = 'Sha256'
            AND content_digest IS NOT NULL AND octet_length(content_digest) = 32));

ALTER TABLE agent_run_log_stream
    DROP CONSTRAINT ck_agent_run_log_stream_terminal,
    ADD CONSTRAINT ck_agent_run_log_stream_terminal CHECK (
        ((state = 'Open' AND completed_at IS NULL AND error_code IS NULL)
            OR (state = 'Completed' AND completed_at IS NOT NULL AND error_code IS NULL)
            OR (state IN ('Truncated', 'Unavailable', 'Corrupt', 'CaptureFailed')
                AND completed_at IS NOT NULL AND error_code IS NOT NULL))
        AND (state <> 'Completed' OR schema_version = 1
            OR (content_digest_algorithm = 'Sha256' AND content_digest IS NOT NULL AND octet_length(content_digest) = 32)));

CREATE OR REPLACE FUNCTION agent_run_log_stream_guard() RETURNS trigger AS $$
DECLARE
    appended agent_run_log_segment%ROWTYPE;
    current_fence BIGINT;
    is_claim BOOLEAN := FALSE;
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
           OR NEW.next_segment_ordinal <> 1 OR NEW.next_offset_bytes <> 0 OR NEW.completed_at IS NOT NULL
           OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL OR NEW.content_digest IS NOT NULL
           OR NEW.content_digest_algorithm IS NOT NULL OR NEW.last_modified_at < NEW.created_at THEN
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
           OR NEW.worker_fence_epoch <= COALESCE(OLD.worker_fence_epoch, 0) OR NEW.capture_session_id IS NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream stale or malformed capture claim rejected (run_id=%, current=%, attempted=%).', NEW.agent_run_id, current_fence, NEW.worker_fence_epoch;
        END IF;
        IF NEW.state IS DISTINCT FROM OLD.state OR NEW.segment_count IS DISTINCT FROM OLD.segment_count
           OR NEW.total_bytes IS DISTINCT FROM OLD.total_bytes OR NEW.next_segment_ordinal IS DISTINCT FROM OLD.next_segment_ordinal
           OR NEW.next_offset_bytes IS DISTINCT FROM OLD.next_offset_bytes OR NEW.completed_at IS DISTINCT FROM OLD.completed_at
           OR NEW.error_code IS DISTINCT FROM OLD.error_code OR NEW.error_message IS DISTINCT FROM OLD.error_message
           OR NEW.content_digest_algorithm IS DISTINCT FROM OLD.content_digest_algorithm
           OR NEW.content_digest IS DISTINCT FROM OLD.content_digest THEN
            RAISE EXCEPTION 'agent_run_log_stream capture claim cannot mutate byte or terminal state (id=%).', OLD.id;
        END IF;
        is_claim := TRUE;
    END IF;

    IF NOT is_claim AND NEW.state = 'Open' THEN
        IF NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
           OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
           OR NEW.segment_count <> OLD.segment_count + 1 OR NEW.next_segment_ordinal <> OLD.next_segment_ordinal + 1
           OR NEW.total_bytes <= OLD.total_bytes OR NEW.next_offset_bytes <> NEW.total_bytes
           OR NEW.completed_at IS NOT NULL OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL
           OR NEW.content_digest_algorithm IS NOT NULL OR NEW.content_digest IS NOT NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream Open updates are exact one-segment head advances (id=%).', OLD.id;
        END IF;

        SELECT * INTO appended FROM agent_run_log_segment
        WHERE team_id = NEW.team_id AND stream_id = NEW.id AND agent_run_id = NEW.agent_run_id
          AND segment_ordinal = OLD.next_segment_ordinal;
        IF NOT FOUND OR appended.start_offset_bytes <> OLD.next_offset_bytes
           OR appended.length_bytes <> NEW.total_bytes - OLD.total_bytes
           OR appended.worker_fence_epoch IS DISTINCT FROM NEW.worker_fence_epoch
           OR appended.capture_session_id IS DISTINCT FROM NEW.capture_session_id
           OR appended.created_at > NEW.last_modified_at THEN
            RAISE EXCEPTION 'agent_run_log_stream head advance requires its exact claimed append-only segment (id=%, ordinal=%).', OLD.id, OLD.next_segment_ordinal;
        END IF;
    ELSIF NOT is_claim THEN
        SELECT fence_epoch INTO current_fence FROM agent_run
        WHERE team_id = NEW.team_id AND id = NEW.agent_run_id
        FOR SHARE;
        IF NOT FOUND OR current_fence <= 0 OR OLD.worker_fence_epoch IS DISTINCT FROM current_fence THEN
            RAISE EXCEPTION 'agent_run_log_stream terminal transition requires its current worker fence (run_id=%, current=%, claimed=%).', NEW.agent_run_id, current_fence, OLD.worker_fence_epoch;
        END IF;
        IF NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
           OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
           OR NEW.segment_count <> OLD.segment_count OR NEW.total_bytes <> OLD.total_bytes
           OR NEW.next_segment_ordinal <> OLD.next_segment_ordinal OR NEW.next_offset_bytes <> OLD.next_offset_bytes
           OR NEW.completed_at IS NULL OR NEW.completed_at < OLD.created_at THEN
            RAISE EXCEPTION 'agent_run_log_stream terminal transition cannot rewrite its claim or byte head (id=%).', OLD.id;
        END IF;
        IF NEW.state = 'Completed' AND (NEW.content_digest_algorithm IS DISTINCT FROM 'Sha256'
           OR NEW.content_digest IS NULL OR octet_length(NEW.content_digest) IS DISTINCT FROM 32) THEN
            RAISE EXCEPTION 'agent_run_log_stream Completed requires its verified SHA-256 content digest (id=%).', OLD.id;
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
    IF NOT FOUND THEN
        RAISE EXCEPTION 'agent_run_log_segment requires its tenant-bound AgentRun (run_id=%).', NEW.agent_run_id;
    END IF;
    IF NEW.worker_fence_epoch <> current_fence OR current_fence <= 0 THEN
        RAISE EXCEPTION 'agent_run_log_segment stale worker fence rejected (run_id=%, current=%, attempted=%).', NEW.agent_run_id, current_fence, NEW.worker_fence_epoch;
    END IF;

    SELECT * INTO stream FROM agent_run_log_stream
    WHERE team_id = NEW.team_id AND id = NEW.stream_id AND agent_run_id = NEW.agent_run_id
    FOR UPDATE;
    IF NOT FOUND OR stream.state <> 'Open' THEN
        RAISE EXCEPTION 'agent_run_log_segment requires its open tenant-bound stream (stream_id=%).', NEW.stream_id;
    END IF;
    IF NEW.worker_fence_epoch IS DISTINCT FROM stream.worker_fence_epoch
       OR NEW.capture_session_id IS DISTINCT FROM stream.capture_session_id THEN
        RAISE EXCEPTION 'agent_run_log_segment capture claim mismatch rejected (stream_id=%).', NEW.stream_id;
    END IF;
    IF NEW.segment_ordinal <> stream.next_segment_ordinal OR NEW.start_offset_bytes <> stream.next_offset_bytes
       OR NEW.schema_version <> stream.schema_version THEN
        RAISE EXCEPTION 'agent_run_log_segment must match the locked next ordinal/offset/schema (stream_id=%, expected_ordinal=%, attempted_ordinal=%, expected_offset=%, attempted_offset=%).',
            NEW.stream_id, stream.next_segment_ordinal, NEW.segment_ordinal, stream.next_offset_bytes, NEW.start_offset_bytes;
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
        ORDER BY location.id
        LIMIT 1
        FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'agent_run_log_segment CAS bytes are not verified Available (object_id=%).', NEW.artifact_object_id;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON COLUMN agent_run_log_stream.worker_fence_epoch IS
    'Latest exact AgentRun worker fence that owns this Open capture stream; a higher current fence may reclaim it.';
COMMENT ON COLUMN agent_run_log_stream.capture_session_id IS
    'Opaque capture writer identity paired with worker_fence_epoch; exact retries reuse it.';
COMMENT ON COLUMN agent_run_log_stream.content_digest IS
    'Verified digest of the complete logical stream, populated only at terminalization.';
