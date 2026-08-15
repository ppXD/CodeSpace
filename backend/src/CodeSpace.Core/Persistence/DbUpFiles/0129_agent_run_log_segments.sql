-- 0129_agent_run_log_segments.sql
--
-- Provider- and harness-neutral durable byte streams for Agent Run stdout, stderr, native transcripts and future
-- debug channels. AgentRun is deliberately standalone-capable, so these are agent_run-owned rather than
-- workflow_run-prefixed. PostgreSQL stores only bounded metadata; each immutable segment references artifact CAS v2.
--
-- A stream is a monotonic head. A segment insert locks the AgentRun first, proves the worker fence is current, locks
-- the stream, proves the exact next ordinal/offset, and accepts only a CAS object with a verified Available location.
-- Its AFTER trigger advances the head in the same transaction. This prevents gaps, overlap, stale-worker writes and
-- "metadata says complete while bytes never arrived" without storing unbounded CLI output in PostgreSQL.
--
-- This migration is schema-only. Existing AgentRunEvent, runner, ArtifactStore, completion and harness paths do not
-- consume these rows until a separately qualified shadow writer is introduced.

ALTER TABLE agent_run
    ADD CONSTRAINT ak_agent_run_team_id UNIQUE (team_id, id);

CREATE TABLE agent_run_log_stream (
    id                      UUID         NOT NULL PRIMARY KEY,
    team_id                 UUID         NOT NULL,
    agent_run_id            UUID         NOT NULL,
    stream_kind             VARCHAR(128) NOT NULL,
    content_type            VARCHAR(255) NOT NULL,
    content_encoding        VARCHAR(64)  NULL,
    capture_source          VARCHAR(128) NOT NULL,
    retention               VARCHAR(24)  NOT NULL,
    expires_at              TIMESTAMPTZ  NULL,
    state                   VARCHAR(24)  NOT NULL,
    revision                BIGINT       NOT NULL,
    segment_count           BIGINT       NOT NULL,
    total_bytes             BIGINT       NOT NULL,
    next_segment_ordinal    BIGINT       NOT NULL,
    next_offset_bytes       BIGINT       NOT NULL,
    schema_version          INTEGER      NOT NULL,
    created_at              TIMESTAMPTZ  NOT NULL,
    last_modified_at        TIMESTAMPTZ  NOT NULL,
    completed_at            TIMESTAMPTZ  NULL,
    error_code              VARCHAR(128) NULL,
    error_message           VARCHAR(2048) NULL,

    CONSTRAINT ak_agent_run_log_stream_scope UNIQUE (team_id, id, agent_run_id),
    CONSTRAINT fk_agent_run_log_stream_run FOREIGN KEY (team_id, agent_run_id)
        REFERENCES agent_run (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_agent_run_log_stream_error CHECK (
        (error_code IS NULL AND error_message IS NULL)
        OR (error_code IS NOT NULL AND btrim(error_code) <> '')),
    CONSTRAINT ck_agent_run_log_stream_head CHECK (
        revision > 0 AND segment_count >= 0 AND total_bytes >= 0
        AND next_segment_ordinal = segment_count + 1 AND next_offset_bytes = total_bytes
        AND schema_version > 0),
    CONSTRAINT ck_agent_run_log_stream_time CHECK (
        last_modified_at >= created_at
        AND (completed_at IS NULL OR last_modified_at >= completed_at)),
    CONSTRAINT ck_agent_run_log_stream_identity CHECK (
        stream_kind ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$'
        AND capture_source ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$'
        AND content_type ~ '^[^[:space:]/]+/[^[:space:]]+$'
        AND (content_encoding IS NULL OR content_encoding ~ '^[a-z0-9][a-z0-9._+-]{0,63}$')),
    CONSTRAINT ck_agent_run_log_stream_retention CHECK (
        retention IN ('Ephemeral', 'Run', 'Team', 'Compliance', 'Permanent')
        AND (expires_at IS NULL OR expires_at > created_at)
        AND (retention <> 'Ephemeral' OR expires_at IS NOT NULL)
        AND (retention <> 'Permanent' OR expires_at IS NULL)),
    CONSTRAINT ck_agent_run_log_stream_state CHECK (
        state IN ('Open', 'Completed', 'Truncated', 'Unavailable', 'Corrupt', 'CaptureFailed')),
    CONSTRAINT ck_agent_run_log_stream_terminal CHECK (
        (state = 'Open' AND completed_at IS NULL AND error_code IS NULL)
        OR (state = 'Completed' AND completed_at IS NOT NULL AND error_code IS NULL)
        OR (state IN ('Truncated', 'Unavailable', 'Corrupt', 'CaptureFailed')
            AND completed_at IS NOT NULL AND error_code IS NOT NULL))
);

CREATE UNIQUE INDEX ux_agent_run_log_stream_kind
    ON agent_run_log_stream (team_id, agent_run_id, stream_kind);
CREATE INDEX ix_agent_run_log_stream_state_modified
    ON agent_run_log_stream (team_id, state, last_modified_at, id);
CREATE INDEX ix_agent_run_log_stream_expiry
    ON agent_run_log_stream (expires_at, id) WHERE expires_at IS NOT NULL;

CREATE TABLE agent_run_log_segment (
    id                      UUID         NOT NULL PRIMARY KEY,
    team_id                 UUID         NOT NULL,
    agent_run_id            UUID         NOT NULL,
    stream_id               UUID         NOT NULL,
    segment_ordinal         BIGINT       NOT NULL,
    start_offset_bytes      BIGINT       NOT NULL,
    length_bytes            BIGINT       NOT NULL,
    artifact_object_id      UUID         NOT NULL,
    worker_fence_epoch      BIGINT       NOT NULL,
    capture_session_id      UUID         NOT NULL,
    first_observed_at       TIMESTAMPTZ  NOT NULL,
    last_observed_at        TIMESTAMPTZ  NOT NULL,
    created_at              TIMESTAMPTZ  NOT NULL,
    schema_version          INTEGER      NOT NULL,

    CONSTRAINT fk_agent_run_log_segment_stream FOREIGN KEY (team_id, stream_id, agent_run_id)
        REFERENCES agent_run_log_stream (team_id, id, agent_run_id) ON DELETE RESTRICT,
    CONSTRAINT fk_agent_run_log_segment_object FOREIGN KEY (team_id, artifact_object_id)
        REFERENCES artifact_object (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_agent_run_log_segment_bounds CHECK (
        segment_ordinal > 0 AND start_offset_bytes >= 0 AND length_bytes > 0
        AND worker_fence_epoch > 0 AND schema_version > 0),
    CONSTRAINT ck_agent_run_log_segment_identity CHECK (
        capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_agent_run_log_segment_observation CHECK (
        first_observed_at <= last_observed_at AND created_at >= last_observed_at)
);

CREATE UNIQUE INDEX ux_agent_run_log_segment_ordinal
    ON agent_run_log_segment (team_id, stream_id, segment_ordinal);
CREATE UNIQUE INDEX ux_agent_run_log_segment_offset
    ON agent_run_log_segment (team_id, stream_id, start_offset_bytes);
CREATE INDEX ix_agent_run_log_segment_object
    ON agent_run_log_segment (team_id, artifact_object_id, id);

CREATE OR REPLACE FUNCTION agent_run_log_stream_guard() RETURNS trigger AS $$
DECLARE
    appended agent_run_log_segment%ROWTYPE;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'agent_run_log_stream is durable capture state — DELETE rejected (id=%).', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.state <> 'Open' OR NEW.revision <> 1 OR NEW.segment_count <> 0 OR NEW.total_bytes <> 0
           OR NEW.next_segment_ordinal <> 1 OR NEW.next_offset_bytes <> 0 OR NEW.completed_at IS NOT NULL
           OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL OR NEW.last_modified_at < NEW.created_at THEN
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

    IF NEW.state = 'Open' THEN
        IF NEW.segment_count <> OLD.segment_count + 1 OR NEW.next_segment_ordinal <> OLD.next_segment_ordinal + 1
           OR NEW.total_bytes <= OLD.total_bytes OR NEW.next_offset_bytes <> NEW.total_bytes
           OR NEW.completed_at IS NOT NULL OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream Open updates are exact one-segment head advances (id=%).', OLD.id;
        END IF;

        SELECT * INTO appended FROM agent_run_log_segment
        WHERE team_id = NEW.team_id AND stream_id = NEW.id AND agent_run_id = NEW.agent_run_id
          AND segment_ordinal = OLD.next_segment_ordinal;
        IF NOT FOUND OR appended.start_offset_bytes <> OLD.next_offset_bytes
           OR appended.length_bytes <> NEW.total_bytes - OLD.total_bytes
           OR appended.created_at > NEW.last_modified_at THEN
            RAISE EXCEPTION 'agent_run_log_stream head advance requires its exact append-only segment (id=%, ordinal=%).', OLD.id, OLD.next_segment_ordinal;
        END IF;
    ELSE
        IF NEW.segment_count <> OLD.segment_count OR NEW.total_bytes <> OLD.total_bytes
           OR NEW.next_segment_ordinal <> OLD.next_segment_ordinal OR NEW.next_offset_bytes <> OLD.next_offset_bytes
           OR NEW.completed_at IS NULL OR NEW.completed_at < OLD.created_at THEN
            RAISE EXCEPTION 'agent_run_log_stream terminal transition cannot rewrite its byte head (id=%).', OLD.id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER agent_run_log_stream_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON agent_run_log_stream
    FOR EACH ROW EXECUTE FUNCTION agent_run_log_stream_guard();

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

CREATE TRIGGER agent_run_log_segment_enforce_append_only
    BEFORE INSERT OR UPDATE OR DELETE ON agent_run_log_segment
    FOR EACH ROW EXECUTE FUNCTION agent_run_log_segment_guard();

CREATE OR REPLACE FUNCTION agent_run_log_segment_advance_head() RETURNS trigger AS $$
BEGIN
    UPDATE agent_run_log_stream SET
        revision = revision + 1,
        segment_count = segment_count + 1,
        total_bytes = total_bytes + NEW.length_bytes,
        next_segment_ordinal = next_segment_ordinal + 1,
        next_offset_bytes = next_offset_bytes + NEW.length_bytes,
        last_modified_at = GREATEST(last_modified_at, NEW.created_at)
    WHERE team_id = NEW.team_id AND id = NEW.stream_id AND agent_run_id = NEW.agent_run_id;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER agent_run_log_segment_advance_stream_head
    AFTER INSERT ON agent_run_log_segment
    FOR EACH ROW EXECUTE FUNCTION agent_run_log_segment_advance_head();

COMMENT ON TABLE agent_run_log_stream IS
    'Monotonic head for one open/versioned Agent Run byte stream. Terminal state describes capture completeness, not task outcome.';
COMMENT ON TABLE agent_run_log_segment IS
    'Append-only contiguous Agent Run log range. Bytes live in verified artifact CAS; worker fence and stream head are checked transactionally.';
