-- 0232_agent_run_log_stream_owner_loss.sql
--
-- A run abandoned on a DEAD host claimed forever that its logs were still finalizing.
--
-- The log capture head (agent_run_log_stream) is Open for the whole life of a run and is moved out of Open by the
-- LIVE worker only: AgentRunLogCaptureBridge completes it, or fails it, from inside the process that owns the run.
-- When that process is gone, nothing does. AgentRunReconcilerService.AbandonAsync terminalizes the RUN — it CASes
-- agent_run to Failed and bumps fence_epoch by one — and touches no log stream at all, so the stream keeps sitting at
-- the SUPERSEDED epoch in state Open. RoomProjector.SummarizeLogs folds any Open stream to Finalizing, so the Room
-- told the operator "finalizing" about a capture whose worker had died minutes or hours earlier, with no deadline and
-- nothing that could ever move it.
--
-- Flipping it needs a statement made by a generation the stream does not belong to, and the terminal arm of
-- agent_run_log_stream_guard() refused exactly that:
--
--     IF NOT FOUND OR current_fence <= 0 OR OLD.worker_fence_epoch IS DISTINCT FROM current_fence THEN RAISE ...
--
-- That equality is right for every transition a live worker makes and is deliberately kept for all of them. This
-- migration adds ONE admissible exception, and makes it as narrow as the shape allows: when the stream's own fence is
-- STRICTLY BEHIND the run's current fence — the stream's generation is provably over, so no live capture session can
-- still be appending to it — the row may move to CaptureFailed, and to nothing else. Every other invariant of the
-- terminal arm still applies unchanged, so the flip cannot rewrite the claim, the byte head, the segment count, the
-- source offsets or the finalization receipt: the durable prefix already committed stays exactly as readable as it was.
--
-- What stays refused, and is pinned by a negative test: a superseded generation may NOT claim Completed (it verified
-- nothing), Truncated, Unavailable or Corrupt (all are statements about the bytes, which it never read), and may not
-- move a stream whose fence is CURRENT through this arm — a live stream is still the live worker's alone.
--
-- Supersedes TWO definitions of agent_run_log_stream_guard(), and reproduces the later one verbatim apart from the
-- widened terminal arm above:
--
--   * 0201_agent_run_log_verification_manifest.sql — the definition this change was originally written against
--     (itself carried forward unchanged from 0132/0133), whose terminal arm is the one quoted above.
--   * 0230_agent_run_log_stream_remote_stall.sql — the definition that is actually installed today. It added the
--     remote_stall_since/remote_stall_code columns and a FIFTH admissible update shape (a remote-stall health
--     statement) to the same function. That arm is reproduced here in full and is NOT narrowed: the merged guard
--     admits every update 0230 admits, plus the one lapsed-fence CaptureFailed transition above.
--
-- This is numbered 0232 rather than 0230 for a reason worth stating, because the failure mode is silent. DbUp is
-- FILENAME-keyed: two migrations numbered 0230 are two different journal rows, so both run, in alphabetical order —
-- and the later filename's CREATE OR REPLACE is the body that survives. The first-written function is then discarded
-- on every database built from scratch, while every database migrated incrementally keeps whichever ran last. A
-- colliding number does not fail; it quietly picks a winner, and no test on either side can see it. One number, one
-- definition of this function.
--
-- No DDL, no new column, no new trigger.
--
-- Rollback: re-run 0230_agent_run_log_stream_remote_stall.sql's CREATE OR REPLACE FUNCTION body (which restores the
-- strict terminal fence equality and keeps the remote-stall arm).

CREATE OR REPLACE FUNCTION agent_run_log_stream_guard() RETURNS trigger AS $$
DECLARE
    appended agent_run_log_segment%ROWTYPE;
    current_fence BIGINT;
    is_claim BOOLEAN := FALSE;
    is_source_finalize BOOLEAN := FALSE;
    is_remote_stall BOOLEAN := FALSE;
    is_owner_loss BOOLEAN := FALSE;
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
        IF NOT FOUND OR current_fence <= 0 THEN
            RAISE EXCEPTION 'agent_run_log_stream terminal transition requires a live run fence (run_id=%, current=%).', NEW.agent_run_id, current_fence;
        END IF;
        -- The owner-loss exception, and the whole of it. A stream whose fence is STRICTLY behind the run's belongs to
        -- a generation that is provably over, so no live capture session can still be appending to it and the only
        -- thing left to say about it is that its owner is gone. CaptureFailed is the one state that says that; every
        -- other terminal state is a claim about the bytes, which a generation that never read them cannot make.
        is_owner_loss := OLD.worker_fence_epoch < current_fence AND NEW.state = 'CaptureFailed';
        IF NOT is_owner_loss AND OLD.worker_fence_epoch IS DISTINCT FROM current_fence THEN
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
