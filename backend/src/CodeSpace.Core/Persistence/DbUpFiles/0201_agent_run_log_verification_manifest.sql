-- 0201_agent_run_log_verification_manifest.sql
-- v3 uses historical full-segment-read receipts and a versioned hash-chain manifest. It does NOT put a manifest
-- root in content_digest, claim simultaneous physical availability, or adopt existing v1/v2 streams.
-- New INSERTs require v3 after this migration; existing v2 streams retain their exact whole-SHA completion contract.

ALTER TABLE agent_run_log_stream ADD COLUMN manifest_digest BYTEA NULL;
ALTER TABLE agent_run_log_stream DROP CONSTRAINT ck_agent_run_log_stream_terminal;
ALTER TABLE agent_run_log_stream ADD CONSTRAINT ck_agent_run_log_stream_terminal CHECK (
    ((state = 'Open' AND completed_at IS NULL AND error_code IS NULL)
      OR (state = 'Completed' AND completed_at IS NOT NULL AND error_code IS NULL)
      OR (state IN ('Truncated', 'Unavailable', 'Corrupt', 'CaptureFailed') AND completed_at IS NOT NULL AND error_code IS NOT NULL))
    AND (state <> 'Completed' OR (capture_finalized_at IS NOT NULL AND
        ((schema_version = 1) OR (schema_version = 2 AND content_digest_algorithm = 'Sha256' AND content_digest IS NOT NULL AND octet_length(content_digest) = 32)
         OR (schema_version = 3 AND manifest_digest IS NOT NULL AND octet_length(manifest_digest) = 32 AND content_digest IS NULL AND content_digest_algorithm IS NULL)))));
ALTER TABLE agent_run_log_stream ADD CONSTRAINT ck_agent_run_log_stream_manifest CHECK (
    manifest_digest IS NULL OR (schema_version = 3 AND state = 'Completed' AND octet_length(manifest_digest) = 32));

CREATE TABLE agent_run_log_verification (
    id UUID NOT NULL PRIMARY KEY,
    team_id UUID NOT NULL,
    agent_run_id UUID NOT NULL,
    stream_id UUID NOT NULL,
    worker_fence_epoch BIGINT NOT NULL,
    capture_session_id UUID NOT NULL,
    stream_revision BIGINT NOT NULL,
    segment_count BIGINT NOT NULL,
    total_bytes BIGINT NOT NULL,
    source_offset_bytes BIGINT NOT NULL,
    next_segment_ordinal BIGINT NOT NULL,
    verified_bytes BIGINT NOT NULL,
    accumulator BYTEA NOT NULL,
    revision BIGINT NOT NULL,
    recovery_intent_id UUID NULL,
    recovery_owner_id UUID NULL,
    recovery_fence_epoch BIGINT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    last_modified_at TIMESTAMPTZ NOT NULL,
    sealed_at TIMESTAMPTZ NULL,
    manifest_digest BYTEA NULL,
    CONSTRAINT fk_agent_run_log_verification_stream FOREIGN KEY (team_id, stream_id, agent_run_id)
        REFERENCES agent_run_log_stream (team_id, id, agent_run_id) ON DELETE RESTRICT,
    CONSTRAINT ck_agent_run_log_verification_bounds CHECK (
        worker_fence_epoch > 0 AND stream_revision > 0 AND segment_count >= 0 AND total_bytes >= 0 AND source_offset_bytes >= 0
        AND next_segment_ordinal > 0 AND next_segment_ordinal <= segment_count + 1 AND verified_bytes >= 0 AND verified_bytes <= total_bytes
        AND revision > 0 AND octet_length(accumulator) = 32 AND last_modified_at >= created_at
        AND (sealed_at IS NULL OR sealed_at <= last_modified_at)),
    CONSTRAINT ck_agent_run_log_verification_claim CHECK (
        (recovery_intent_id IS NULL AND recovery_owner_id IS NULL AND recovery_fence_epoch IS NULL)
        OR (recovery_intent_id IS NOT NULL AND recovery_owner_id IS NOT NULL AND recovery_fence_epoch > 0)),
    CONSTRAINT ck_agent_run_log_verification_seal CHECK (
        (sealed_at IS NULL AND manifest_digest IS NULL)
        OR (sealed_at IS NOT NULL AND manifest_digest IS NOT NULL AND octet_length(manifest_digest) = 32
            AND next_segment_ordinal = segment_count + 1 AND verified_bytes = total_bytes))
);
CREATE UNIQUE INDEX ux_agent_run_log_verification_head ON agent_run_log_verification (team_id, stream_id, stream_revision);

-- Domain-separated standard SHA-256 calls over canonical fixed-width network-order values. This is a manifest
-- hash chain, not exported SHA internal state. uuid_send/int8send match the documented wire encoding in C#.
CREATE FUNCTION agent_run_log_manifest_begin(value agent_run_log_verification) RETURNS bytea AS $$
    SELECT sha256(convert_to('codespace.agent-run-log.manifest/header/v1', 'UTF8') || decode('00', 'hex')
        || uuid_send(value.team_id) || uuid_send(value.agent_run_id) || uuid_send(value.stream_id) || int8send(value.worker_fence_epoch)
        || uuid_send(value.capture_session_id) || int8send(value.stream_revision) || int8send(value.segment_count)
        || int8send(value.total_bytes) || int8send(value.source_offset_bytes))
$$ LANGUAGE SQL IMMUTABLE STRICT;

CREATE FUNCTION agent_run_log_manifest_append(previous bytea, part agent_run_log_segment, content_digest bytea) RETURNS bytea AS $$
    SELECT sha256(convert_to('codespace.agent-run-log.manifest/entry/v1', 'UTF8') || decode('00', 'hex') || previous
        || int8send(part.segment_ordinal) || int8send(part.start_offset_bytes) || int8send(part.length_bytes) || uuid_send(part.artifact_object_id) || content_digest)
$$ LANGUAGE SQL IMMUTABLE STRICT;

CREATE FUNCTION agent_run_log_manifest_seal(value agent_run_log_verification) RETURNS bytea AS $$
    SELECT sha256(convert_to('codespace.agent-run-log.manifest/seal/v1', 'UTF8') || decode('00', 'hex')
        || value.accumulator || int8send(value.segment_count) || int8send(value.total_bytes))
$$ LANGUAGE SQL IMMUTABLE STRICT;

CREATE FUNCTION agent_run_log_verification_guard() RETURNS trigger AS $$
DECLARE
    head agent_run_log_stream%ROWTYPE;
    part agent_run_log_segment%ROWTYPE;
    part_digest BYTEA;
    current_fence BIGINT;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Log verification is durable history; deletion is rejected.'; END IF;
    SELECT fence_epoch INTO current_fence FROM agent_run WHERE team_id = NEW.team_id AND id = NEW.agent_run_id FOR SHARE;
    IF NOT FOUND OR current_fence IS DISTINCT FROM NEW.worker_fence_epoch THEN
        RAISE SQLSTATE 'P0111' USING MESSAGE = 'Log verification worker fence is stale.';
    END IF;
    SELECT * INTO head FROM agent_run_log_stream WHERE team_id = NEW.team_id AND id = NEW.stream_id AND agent_run_id = NEW.agent_run_id FOR SHARE;
    IF NOT FOUND OR head.schema_version <> 3 OR head.state <> 'Open' OR head.capture_finalized_at IS NULL
       OR head.worker_fence_epoch IS DISTINCT FROM NEW.worker_fence_epoch OR head.capture_session_id IS DISTINCT FROM NEW.capture_session_id
       OR head.revision <> NEW.stream_revision OR head.segment_count <> NEW.segment_count OR head.total_bytes <> NEW.total_bytes
       OR head.source_offset_bytes <> NEW.source_offset_bytes THEN
        RAISE SQLSTATE 'P0112' USING MESSAGE = 'Log verification does not match the exact finalized stream head.';
    END IF;
    IF NEW.recovery_intent_id IS NOT NULL THEN
        PERFORM intent.id FROM agent_run_log_capture_intent intent
        WHERE intent.id = NEW.recovery_intent_id AND intent.team_id = NEW.team_id AND intent.agent_run_id = NEW.agent_run_id
          AND intent.worker_fence_epoch = NEW.worker_fence_epoch AND intent.capture_session_id = NEW.capture_session_id
          AND intent.stream_kind = head.stream_kind AND intent.content_type = head.content_type
          AND intent.content_encoding IS NOT DISTINCT FROM head.content_encoding AND intent.capture_source = head.capture_source
          AND (intent.stream_id IS NULL OR intent.stream_id = NEW.stream_id)
          AND intent.state IN ('Expected', 'Opened', 'SourceFinalized')
          AND intent.recovery_owner_id = NEW.recovery_owner_id AND intent.recovery_fence_epoch = NEW.recovery_fence_epoch
          AND intent.recovery_lease_expires_at > clock_timestamp() FOR SHARE;
        IF NOT FOUND THEN
            RAISE SQLSTATE 'P0113' USING MESSAGE = 'Log verification recovery owner or lease is stale.';
        END IF;
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW.next_segment_ordinal <> 1 OR NEW.verified_bytes <> 0 OR NEW.revision <> 1 OR NEW.sealed_at IS NOT NULL
           OR NEW.manifest_digest IS NOT NULL OR NEW.accumulator IS DISTINCT FROM agent_run_log_manifest_begin(NEW) THEN
            RAISE EXCEPTION 'Log verification must begin at the exact canonical empty prefix.';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD.sealed_at IS NOT NULL OR NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.agent_run_id IS DISTINCT FROM OLD.agent_run_id OR NEW.stream_id IS DISTINCT FROM OLD.stream_id
       OR NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
       OR NEW.stream_revision IS DISTINCT FROM OLD.stream_revision OR NEW.segment_count IS DISTINCT FROM OLD.segment_count
       OR NEW.total_bytes IS DISTINCT FROM OLD.total_bytes OR NEW.source_offset_bytes IS DISTINCT FROM OLD.source_offset_bytes
       OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
        RAISE EXCEPTION 'Log verification identity is immutable and revisions must advance exactly.';
    END IF;
    IF NEW.sealed_at IS NOT NULL THEN
        IF NEW.next_segment_ordinal <> OLD.next_segment_ordinal OR NEW.verified_bytes <> OLD.verified_bytes
           OR NEW.accumulator IS DISTINCT FROM OLD.accumulator OR NEW.next_segment_ordinal <> NEW.segment_count + 1
           OR NEW.verified_bytes <> NEW.total_bytes OR NEW.manifest_digest IS DISTINCT FROM agent_run_log_manifest_seal(NEW) THEN
            RAISE EXCEPTION 'Log verification seal must cover the exact complete prefix.';
        END IF;
        RETURN NEW;
    END IF;
    SELECT * INTO part FROM agent_run_log_segment WHERE team_id = NEW.team_id AND stream_id = NEW.stream_id
        AND segment_ordinal = OLD.next_segment_ordinal;
    IF NOT FOUND THEN RAISE EXCEPTION 'Log verification cannot skip a missing segment.'; END IF;
    SELECT digest INTO part_digest FROM artifact_object WHERE team_id = NEW.team_id AND id = part.artifact_object_id;
    IF NEW.next_segment_ordinal <> OLD.next_segment_ordinal + 1 OR part.start_offset_bytes <> OLD.verified_bytes
       OR NEW.verified_bytes <> OLD.verified_bytes + part.length_bytes OR NEW.manifest_digest IS NOT NULL
       OR NEW.accumulator IS DISTINCT FROM agent_run_log_manifest_append(OLD.accumulator, part, part_digest) THEN
        RAISE EXCEPTION 'Log verification may only advance one exact canonical segment.';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER agent_run_log_verification_enforce_invariants BEFORE INSERT OR UPDATE OR DELETE ON agent_run_log_verification
    FOR EACH ROW EXECUTE FUNCTION agent_run_log_verification_guard();

ALTER TABLE agent_run_log_capture_intent ADD COLUMN verification_progress_ordinal BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN verification_stalled_attempts INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN verification_claim_marker BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN last_verification_progress_at TIMESTAMPTZ NULL;
ALTER TABLE agent_run_log_capture_intent ADD CONSTRAINT ck_agent_run_log_capture_intent_verification_progress CHECK (
    verification_progress_ordinal >= 0 AND verification_stalled_attempts >= 0 AND verification_claim_marker >= 0
    AND ((verification_progress_ordinal = 0 AND last_verification_progress_at IS NULL)
         OR (verification_progress_ordinal > 0 AND last_verification_progress_at IS NOT NULL)));

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
        IF NEW.manifest_digest IS NOT NULL THEN
            RAISE EXCEPTION 'A new log stream cannot carry a manifest receipt.';
        END IF;
        SELECT fence_epoch INTO current_fence FROM agent_run
        WHERE team_id = NEW.team_id AND id = NEW.agent_run_id
        FOR SHARE;
        IF NOT FOUND OR current_fence <= 0 OR NEW.worker_fence_epoch IS DISTINCT FROM current_fence
           OR NEW.capture_session_id IS NULL OR NEW.schema_version <> 3 THEN
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

    IF NEW.manifest_digest IS DISTINCT FROM OLD.manifest_digest
       AND NOT (OLD.state = 'Open' AND NEW.state = 'Completed' AND NEW.schema_version = 3) THEN
        RAISE EXCEPTION 'A log manifest receipt may only be set by v3 completion.';
    END IF;
    IF NEW.schema_version = 3 AND NEW.state = 'Completed' THEN
        IF NEW.content_digest IS NOT NULL OR NEW.content_digest_algorithm IS NOT NULL OR NEW.manifest_digest IS NULL
           OR NOT EXISTS (
               SELECT 1 FROM agent_run_log_verification verification
               WHERE verification.team_id = NEW.team_id AND verification.agent_run_id = NEW.agent_run_id
                 AND verification.stream_id = NEW.id AND verification.stream_revision = OLD.revision
                 AND verification.worker_fence_epoch = NEW.worker_fence_epoch AND verification.capture_session_id = NEW.capture_session_id
                 AND verification.segment_count = NEW.segment_count AND verification.total_bytes = NEW.total_bytes
                 AND verification.source_offset_bytes = NEW.source_offset_bytes AND verification.sealed_at IS NOT NULL
                 AND verification.next_segment_ordinal = NEW.segment_count + 1 AND verification.verified_bytes = NEW.total_bytes
                 AND verification.manifest_digest = NEW.manifest_digest) THEN
            RAISE EXCEPTION 'v3 completion requires its exact sealed segment manifest; a whole SHA is not a manifest receipt.';
        END IF;
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
        IF NEW.state = 'Completed' AND NEW.schema_version <> 3 AND (NEW.content_digest_algorithm IS DISTINCT FROM 'Sha256'
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


-- Preserve legacy capture guards and add progress/protocol fencing for v3 only.
CREATE OR REPLACE FUNCTION agent_run_log_capture_intent_guard() RETURNS trigger AS $$
DECLARE
    current_fence BIGINT;
    current_status VARCHAR(16);
    linked_stream agent_run_log_stream%ROWTYPE;
    is_claim BOOLEAN := FALSE;
    is_manifest BOOLEAN := FALSE;
    observed_progress BIGINT := 0;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'agent_run_log_capture_intent is a durable monotonic ledger — DELETE rejected (id=%).', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        SELECT fence_epoch, status INTO current_fence, current_status FROM agent_run
        WHERE team_id = NEW.team_id AND id = NEW.agent_run_id FOR SHARE;
        IF NOT FOUND OR current_status <> 'Running' OR current_fence <= 0
           OR NEW.worker_fence_epoch IS DISTINCT FROM current_fence THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent requires its exact current Running AgentRun fence (run_id=%, attempted_fence=%, current_fence=%).', NEW.agent_run_id, NEW.worker_fence_epoch, current_fence;
        END IF;
        IF NEW.verification_progress_ordinal <> 0 OR NEW.verification_stalled_attempts <> 0
           OR NEW.verification_claim_marker <> 0 OR NEW.last_verification_progress_at IS NOT NULL THEN
            RAISE EXCEPTION 'Capture intent must begin without verification progress.';
        END IF;
        IF NEW.state <> 'Expected' OR NEW.revision <> 1 OR NEW.stream_id IS NOT NULL
           OR NEW.recovery_attempt_count <> 0 OR NEW.recovery_started_at IS NOT NULL OR NEW.recovery_owner_id IS NOT NULL
           OR NEW.recovery_fence_epoch <> 0 OR NEW.recovery_lease_expires_at IS NOT NULL
           OR NEW.last_error_code IS NOT NULL OR NEW.last_error_message IS NOT NULL OR NEW.terminal_at IS NOT NULL
           OR NEW.terminal_observed_at IS NOT NULL
           OR NEW.last_modified_at IS DISTINCT FROM NEW.created_at OR NEW.next_recovery_at < NEW.created_at THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent must start as an unclaimed Expected revision-one row (id=%).', NEW.id;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.agent_run_id IS DISTINCT FROM OLD.agent_run_id
       OR NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
       OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
       OR NEW.stream_kind IS DISTINCT FROM OLD.stream_kind OR NEW.content_type IS DISTINCT FROM OLD.content_type
       OR NEW.content_encoding IS DISTINCT FROM OLD.content_encoding OR NEW.capture_source IS DISTINCT FROM OLD.capture_source
       OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'agent_run_log_capture_intent stable expectation identity is immutable (id=%).', OLD.id;
    END IF;
    IF OLD.state IN ('Completed', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate') THEN
        RAISE EXCEPTION 'agent_run_log_capture_intent terminal state is immutable (id=%, state=%).', OLD.id, OLD.state;
    END IF;
    IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
        RAISE EXCEPTION 'agent_run_log_capture_intent revision/time must advance exactly once (id=%).', OLD.id;
    END IF;

    SELECT EXISTS (SELECT 1 FROM agent_run_log_stream stream WHERE stream.team_id = NEW.team_id
        AND stream.agent_run_id = NEW.agent_run_id AND stream.stream_kind = NEW.stream_kind AND stream.schema_version = 3) INTO is_manifest;
    IF NEW.recovery_fence_epoch IS DISTINCT FROM OLD.recovery_fence_epoch THEN
        IF NEW.verification_progress_ordinal IS DISTINCT FROM OLD.verification_progress_ordinal
           OR NEW.verification_stalled_attempts IS DISTINCT FROM OLD.verification_stalled_attempts
           OR NEW.last_verification_progress_at IS DISTINCT FROM OLD.last_verification_progress_at
           OR NEW.verification_claim_marker <> OLD.verification_claim_marker + (CASE WHEN is_manifest THEN 1 ELSE 0 END) THEN
            RAISE EXCEPTION 'v3 recovery requires an explicit next verification protocol marker; claims cannot alter verification progress.';
        END IF;
        IF OLD.recovery_lease_expires_at > clock_timestamp()
           OR NEW.recovery_fence_epoch <> OLD.recovery_fence_epoch + 1
           OR NEW.recovery_owner_id IS NULL OR NEW.recovery_lease_expires_at <= clock_timestamp()
           OR NEW.recovery_attempt_count <> OLD.recovery_attempt_count + 1 THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent recovery claim must own the next fence with a live lease (id=%).', OLD.id;
        END IF;
        IF NEW.state IS DISTINCT FROM OLD.state OR NEW.stream_id IS DISTINCT FROM OLD.stream_id
           OR NEW.next_recovery_at IS DISTINCT FROM OLD.next_recovery_at
           OR NEW.last_error_code IS DISTINCT FROM OLD.last_error_code
           OR NEW.last_error_message IS DISTINCT FROM OLD.last_error_message
           OR NEW.terminal_observed_at IS DISTINCT FROM OLD.terminal_observed_at
           OR NEW.terminal_at IS DISTINCT FROM OLD.terminal_at THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent recovery claim cannot mutate capture state (id=%).', OLD.id;
        END IF;
        IF OLD.recovery_started_at IS NULL THEN
            IF NEW.recovery_started_at IS NULL OR NEW.recovery_started_at > NEW.last_modified_at THEN
                RAISE EXCEPTION 'agent_run_log_capture_intent first claim must arm its DB-clock recovery age (id=%).', OLD.id;
            END IF;
        ELSIF NEW.recovery_started_at IS DISTINCT FROM OLD.recovery_started_at THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent recovery age is immutable after first claim (id=%).', OLD.id;
        END IF;
        is_claim := TRUE;
    END IF;

    IF NOT is_claim THEN
        IF NEW.verification_claim_marker IS DISTINCT FROM OLD.verification_claim_marker THEN
            RAISE EXCEPTION 'Recovery settlement cannot rewrite its verification protocol marker.';
        END IF;
        IF is_manifest THEN
            SELECT COALESCE(MAX(verification.next_segment_ordinal - 1), 0) INTO observed_progress
            FROM agent_run_log_verification verification JOIN agent_run_log_stream stream
              ON stream.team_id = verification.team_id AND stream.id = verification.stream_id
            WHERE verification.team_id = NEW.team_id AND verification.agent_run_id = NEW.agent_run_id
              AND verification.worker_fence_epoch = NEW.worker_fence_epoch AND verification.capture_session_id = NEW.capture_session_id
              AND stream.stream_kind = NEW.stream_kind;
            IF observed_progress > OLD.verification_progress_ordinal THEN
                IF NEW.verification_progress_ordinal <> observed_progress OR NEW.verification_stalled_attempts <> 0
                   OR NEW.last_verification_progress_at IS DISTINCT FROM NEW.last_modified_at THEN
                    RAISE EXCEPTION 'Recovery progress must match the exact durable verification checkpoint.';
                END IF;
            ELSE
                IF NEW.verification_progress_ordinal IS DISTINCT FROM OLD.verification_progress_ordinal
                   OR NEW.last_verification_progress_at IS DISTINCT FROM OLD.last_verification_progress_at
                   OR NEW.verification_stalled_attempts <> OLD.verification_stalled_attempts + (CASE
                       WHEN (NEW.state IN ('Expected', 'Opened', 'SourceFinalized') OR NEW.last_error_code = 'recovery-exhausted') AND NEW.last_error_code IS DISTINCT FROM 'terminal-grace-armed' THEN 1 ELSE 0 END) THEN
                    RAISE EXCEPTION 'Recovery cannot invent progress or erase a no-progress attempt.';
                END IF;
            END IF;
        ELSIF NEW.verification_progress_ordinal IS DISTINCT FROM OLD.verification_progress_ordinal
           OR NEW.verification_stalled_attempts IS DISTINCT FROM OLD.verification_stalled_attempts
           OR NEW.last_verification_progress_at IS DISTINCT FROM OLD.last_verification_progress_at THEN
            RAISE EXCEPTION 'Legacy recovery cannot claim v3 verification progress.';
        END IF;
        IF OLD.recovery_owner_id IS NULL OR OLD.recovery_lease_expires_at IS NULL
           OR OLD.recovery_lease_expires_at <= clock_timestamp()
           OR NEW.recovery_owner_id IS NOT NULL OR NEW.recovery_lease_expires_at IS NOT NULL
           OR NEW.recovery_fence_epoch IS DISTINCT FROM OLD.recovery_fence_epoch
           OR NEW.recovery_attempt_count IS DISTINCT FROM OLD.recovery_attempt_count
           OR NEW.recovery_started_at IS DISTINCT FROM OLD.recovery_started_at THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent outcome requires and releases its exact live recovery lease (id=%).', OLD.id;
        END IF;
        SELECT fence_epoch, status INTO current_fence, current_status FROM agent_run
        WHERE team_id = OLD.team_id AND id = OLD.agent_run_id FOR SHARE;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent outcome lost its AgentRun (id=%).', OLD.id;
        END IF;
        IF current_fence IS DISTINCT FROM OLD.worker_fence_epoch AND NEW.state <> 'Superseded' THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent non-superseded outcome requires the exact current AgentRun fence (id=%, expected=%, current=%).', OLD.id, OLD.worker_fence_epoch, current_fence;
        END IF;
        IF OLD.stream_id IS NOT NULL AND NEW.stream_id IS DISTINCT FROM OLD.stream_id THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent linked stream is immutable (id=%).', OLD.id;
        END IF;
        IF OLD.terminal_observed_at IS NULL AND NEW.terminal_observed_at IS NOT NULL THEN
            IF current_status = 'Running' OR NEW.state NOT IN ('Expected', 'Opened', 'SourceFinalized') THEN
                RAISE EXCEPTION 'agent_run_log_capture_intent terminal grace can only be armed by a nonterminal retry after AgentRun terminal observation (id=%).', OLD.id;
            END IF;
        ELSIF NEW.terminal_observed_at IS DISTINCT FROM OLD.terminal_observed_at THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent terminal observation is immutable once armed (id=%).', OLD.id;
        END IF;
        IF NOT (
            (OLD.state = 'Expected' AND NEW.state IN ('Expected', 'Opened', 'SourceFinalized', 'Completed', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate'))
            OR (OLD.state = 'Opened' AND NEW.state IN ('Opened', 'SourceFinalized', 'Completed', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate'))
            OR (OLD.state = 'SourceFinalized' AND NEW.state IN ('SourceFinalized', 'Completed', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate'))
        ) THEN
            RAISE EXCEPTION 'agent_run_log_capture_intent illegal monotonic state transition (id=%, old=%, new=%).', OLD.id, OLD.state, NEW.state;
        END IF;
        IF NEW.state IN ('Expected', 'Opened', 'SourceFinalized') THEN
            IF NEW.terminal_at IS NOT NULL
               OR (NEW.last_error_code = 'terminal-grace-armed'
                   AND (NEW.terminal_observed_at IS NULL OR NEW.next_recovery_at < NEW.terminal_observed_at))
               OR (NEW.last_error_code <> 'terminal-grace-armed' AND NEW.next_recovery_at <= clock_timestamp())
               OR (NEW.last_error_code IS NULL AND NOT (is_manifest AND NEW.verification_progress_ordinal > OLD.verification_progress_ordinal AND NEW.next_recovery_at > clock_timestamp())) THEN
                RAISE EXCEPTION 'agent_run_log_capture_intent retry outcome requires a typed future retry (id=%).', OLD.id;
            END IF;
        ELSE
            IF NEW.terminal_at IS NULL OR NEW.next_recovery_at IS DISTINCT FROM OLD.next_recovery_at THEN
                RAISE EXCEPTION 'agent_run_log_capture_intent terminal outcome requires one terminal timestamp and no reschedule (id=%).', OLD.id;
            END IF;
            IF NEW.state = 'Completed' AND (NEW.stream_id IS NULL OR NEW.last_error_code IS NOT NULL OR NEW.last_error_message IS NOT NULL) THEN
                RAISE EXCEPTION 'agent_run_log_capture_intent Completed requires a linked stream and no error (id=%).', OLD.id;
            ELSIF NEW.state IN ('CaptureFailed', 'Superseded', 'ExternalStateIndeterminate') AND NEW.last_error_code IS NULL THEN
                RAISE EXCEPTION 'agent_run_log_capture_intent non-success terminal outcome requires a typed reason (id=%).', OLD.id;
            END IF;
        END IF;
        IF NEW.stream_id IS NOT NULL AND NEW.state <> 'Superseded' THEN
            SELECT * INTO linked_stream FROM agent_run_log_stream
            WHERE team_id = OLD.team_id AND agent_run_id = OLD.agent_run_id AND id = NEW.stream_id FOR SHARE;
            IF NOT FOUND OR linked_stream.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
               OR linked_stream.capture_session_id IS DISTINCT FROM OLD.capture_session_id
               OR linked_stream.stream_kind IS DISTINCT FROM OLD.stream_kind
               OR linked_stream.content_type IS DISTINCT FROM OLD.content_type
               OR linked_stream.content_encoding IS DISTINCT FROM OLD.content_encoding
               OR linked_stream.capture_source IS DISTINCT FROM OLD.capture_source THEN
                RAISE EXCEPTION 'agent_run_log_capture_intent stream admission requires its exact immutable expectation identity (id=%, stream_id=%).', OLD.id, NEW.stream_id;
            END IF;
            IF NEW.state = 'Completed' THEN
                IF linked_stream.state <> 'Completed' THEN
                    RAISE EXCEPTION 'agent_run_log_capture_intent Completed requires an exact Completed stream (id=%, stream_id=%).', OLD.id, NEW.stream_id;
                END IF;
                PERFORM 1 FROM agent_run_log_capture_session
                WHERE team_id = OLD.team_id AND agent_run_id = OLD.agent_run_id AND stream_id = NEW.stream_id
                  AND capture_session_id = OLD.capture_session_id AND state = 'Finalized' FOR SHARE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'agent_run_log_capture_intent Completed requires its exact Finalized capture session (id=%, stream_id=%).', OLD.id, NEW.stream_id;
                END IF;
            END IF;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
