-- Lets a log stream say its bytes are HELD rather than lost while its remote storage is transiently refusing them.
--
-- The capture bridge had one answer to a provider that would not accept a segment: spend the append's operation
-- budget, terminalize the stream, and drop the queued bytes. The tail of a run's log was therefore lost to a fault the
-- provider itself reported as retryable, and the stream then read as "Open, finalizing" to every reader -- the exact
-- silence that makes a green run untrustworthy. Backpressure is the fix: no offset advances, no segment is dropped,
-- the sandbox spool keeps being the buffer it already is, and THESE two columns are what makes the wait legible.
--
-- Both columns move together or neither does. A start with no cause is a shrug; a cause with no start cannot be aged
-- out against the park ceiling that eventually names the loss.
--
-- They are NOT part of the monotonic head: a stall statement is a side channel over an Open stream's health, never a
-- claim about its content. It is still a revisioned write like every other update to this table (the guard below
-- demands revision = OLD.revision + 1, and the writer touches last_modified_at with it) because a side channel exempt
-- from the monotonic rule is a row two writers can disagree about. The producer therefore re-reads the head it now
-- needs instead of carrying the revision it held before the outage.
ALTER TABLE agent_run_log_stream ADD COLUMN remote_stall_since timestamptz NULL;
ALTER TABLE agent_run_log_stream ADD COLUMN remote_stall_code  varchar(128) NULL;

ALTER TABLE agent_run_log_stream ADD CONSTRAINT ck_agent_run_log_stream_remote_stall CHECK (
    (remote_stall_since IS NULL AND remote_stall_code IS NULL)
    OR (remote_stall_since IS NOT NULL AND remote_stall_code IS NOT NULL AND btrim(remote_stall_code) <> ''));

COMMENT ON COLUMN agent_run_log_stream.remote_stall_since IS
    'When the producer''s remote first refused a segment it is still holding. Frozen head plus this column means backpressure, not progress; NULL means nothing is queued behind an outage.';
COMMENT ON COLUMN agent_run_log_stream.remote_stall_code IS
    'The typed refusal being waited out, in the capture bridge''s own error-code vocabulary. Never parsed, and never a second reason vocabulary.';
COMMENT ON CONSTRAINT ck_agent_run_log_stream_remote_stall ON agent_run_log_stream IS
    'A stall is a start AND a cause, or it is nothing at all.';

-- The guard has to learn the shape, or the statement is refused as an append that never happened.
--
-- agent_run_log_stream_guard() admits exactly FOUR kinds of UPDATE: a capture claim, a source finalization, an exact
-- one-segment head advance, and a terminal transition. Every one of them demands the revision advance, and the
-- head-advance arm additionally demands segment_count = OLD + 1 and a matching append-only segment row -- so an
-- update that moved only the two columns above raised 'Open updates are exact one-segment head advances' and the
-- producer could not state its own health at all.
--
-- This redefines the function (the same way 0132, 0133 and 0201 each did) with a FIFTH arm for that statement. The
-- revision still advances, because a side channel exempt from the monotonic rule is a row two writers can disagree
-- about; the caller re-reads the head it now needs. Everything else about the row is required to be untouched, which
-- is what keeps 'my bytes are queued' from ever being mistakable for 'my bytes are stored'. An INSERT may not be born
-- stalled either: a stream nobody has tried to write to yet is not waiting on anything.
CREATE OR REPLACE FUNCTION agent_run_log_stream_guard() RETURNS trigger AS $$
DECLARE
    appended agent_run_log_segment%ROWTYPE;
    current_fence BIGINT;
    is_claim BOOLEAN := FALSE;
    is_source_finalize BOOLEAN := FALSE;
    is_remote_stall BOOLEAN := FALSE;
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
           OR NEW.remote_stall_since IS NOT NULL OR NEW.remote_stall_code IS NOT NULL
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
        -- The two stall columns are deliberately ABSENT from that list: a claim is the one statement that may clear
        -- them. A marker is one producer's statement about a segment it holds in memory, and a claim supersedes that
        -- producer -- so a marker the claim inherited would have no one left to clear it and every reader would call
        -- a healthily-capturing stream stalled forever. Do not add them here.
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

    -- The FIFTH admissible update shape, and the reason this function is redefined: a REMOTE-STALL statement. The
    -- four shapes above are a claim, a source finalization, an exact one-segment head advance, and a terminal
    -- transition -- so a producer holding a segment behind a transient outage had no legal way to SAY so, and its
    -- statement was refused as an append it never made. This arm admits exactly that statement and nothing more: the
    -- two stall columns move, the revision advances as every update here must, and every claim, byte-head and
    -- terminal column is required to be untouched. That is what keeps "my bytes are queued" from ever being
    -- mistakable for "my bytes are stored".
    IF NOT is_claim AND NOT is_source_finalize AND NEW.state = 'Open'
       AND (NEW.remote_stall_since IS DISTINCT FROM OLD.remote_stall_since
            OR NEW.remote_stall_code IS DISTINCT FROM OLD.remote_stall_code) THEN
        IF NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
           OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
           OR NEW.segment_count <> OLD.segment_count OR NEW.total_bytes <> OLD.total_bytes
           OR NEW.source_offset_bytes <> OLD.source_offset_bytes
           OR NEW.capture_source_base_offset_bytes <> OLD.capture_source_base_offset_bytes
           OR NEW.capture_finalized_at IS DISTINCT FROM OLD.capture_finalized_at
           OR NEW.next_segment_ordinal <> OLD.next_segment_ordinal OR NEW.next_offset_bytes <> OLD.next_offset_bytes
           OR NEW.completed_at IS NOT NULL OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL
           OR NEW.content_digest_algorithm IS NOT NULL OR NEW.content_digest IS NOT NULL
           OR NEW.manifest_digest IS DISTINCT FROM OLD.manifest_digest THEN
            RAISE EXCEPTION 'agent_run_log_stream remote-stall statement cannot rewrite its claim, byte head or terminal state (id=%).', OLD.id;
        END IF;
        is_remote_stall := TRUE;
    END IF;

    IF NOT is_claim AND NOT is_source_finalize AND NOT is_remote_stall AND NEW.state = 'Open' THEN
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
    ELSIF NOT is_claim AND NOT is_source_finalize AND NOT is_remote_stall THEN
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
