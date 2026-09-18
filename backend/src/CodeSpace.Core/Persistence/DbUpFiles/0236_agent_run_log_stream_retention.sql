-- 0236_agent_run_log_stream_retention.sql
--
-- A terminal log stream could not be told that its bytes are gone.
--
-- agent_run_log_stream_guard() admits five update shapes (a capture claim, a source finalization, a remote-stall
-- statement, an exact one-segment head advance, a terminal transition) and refuses everything else outright:
--
--     IF OLD.state <> 'Open' THEN RAISE EXCEPTION 'agent_run_log_stream terminal state is immutable (id=%).' ...
--
-- That is right for every statement a capturing worker makes, and it is kept for all of them. But it also means the
-- retention columns 0235 added can never be written: a stream is a retention candidate only once it is terminal, and
-- from that moment the row admits no update at all. DELETE is refused too, and deliberately so — the head row is the
-- tombstone that lets a reader say "purged" instead of finding nothing.
--
-- This migration adds ONE admissible shape, the sixth, and makes it as narrow as the row allows: a RETENTION
-- STATEMENT may move retain_until and purged_at on a terminal stream and NOTHING else. It cannot change the state, the
-- claim, the byte head, the source offsets, the finalization receipt, the digests or the error — so a purge can never
-- be mistaken for a capture verdict, and the durable prefix's metadata stays exactly as readable as it was. Three
-- further rules ride with it, each of which exists because its absence is silent data loss:
--
--   * A stream that is still Open is never a retention candidate. Its bytes belong to a live capture session, so the
--     columns are refused there rather than left to fall through an arm that does not enumerate them.
--   * A purge requires a retain_until that was already recorded. The reaper's two waits are an age floor and then a
--     quarantine, and the second one only exists if it was durably written first; without this rule one sweep could
--     both propose and execute a collection.
--   * A purge is final. Clearing purged_at would claim bytes are back that no one restored.
--
-- The INSERT arm is tightened for the same reason it rejects a pre-set manifest receipt: a new stream that arrives
-- already carrying a retention verdict is not a new stream.
--
-- Supersedes 0232_agent_run_log_stream_owner_loss.sql, which is reproduced verbatim apart from the arm above and the
-- INSERT clause. The function has been redefined at 0129, 0132, 0133, 0201, 0230 and 0232 — one number, one
-- definition, every time (MigrationDiscoveryTests.No_two_migrations_at_one_number_redefine_the_same_function), because
-- DbUp is FILENAME-keyed: two files at one number both run and the later NAME silently wins.
--
-- No DDL, no new column, no new trigger.
--
-- Rollback: re-run 0232_agent_run_log_stream_owner_loss.sql's CREATE OR REPLACE FUNCTION body. Any stream already
-- carrying purged_at keeps it; nothing can write another one.

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
           OR NEW.retain_until IS NOT NULL OR NEW.purged_at IS NOT NULL
           OR NEW.last_modified_at < NEW.created_at THEN
            RAISE EXCEPTION 'agent_run_log_stream must start as an empty Open revision-one head (id=%).', NEW.id;
        END IF;
        RETURN NEW;
    END IF;

    -- The SIXTH admissible update shape, and the reason this function is redefined: a RETENTION STATEMENT. It is
    -- matched first and returns on its own, because the arms below are written for statements a capturing worker
    -- makes and every one of them refuses a terminal row. Both columns are named explicitly, so a statement that
    -- touches neither never reaches this arm at all.
    IF NEW.retain_until IS DISTINCT FROM OLD.retain_until OR NEW.purged_at IS DISTINCT FROM OLD.purged_at THEN
        IF OLD.state = 'Open' THEN
            RAISE EXCEPTION 'agent_run_log_stream retention statement rejected on a live stream (id=%); its bytes belong to an open capture session.', OLD.id;
        END IF;
        IF OLD.purged_at IS NOT NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream purge is final; a purged stream admits no further retention statement (id=%).', OLD.id;
        END IF;
        IF NEW.purged_at IS NOT NULL AND NEW.retain_until IS NULL THEN
            RAISE EXCEPTION 'agent_run_log_stream cannot be purged without the retain_until it was quarantined under (id=%).', OLD.id;
        END IF;
        IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
           OR NEW.agent_run_id IS DISTINCT FROM OLD.agent_run_id OR NEW.state IS DISTINCT FROM OLD.state
           OR NEW.stream_kind IS DISTINCT FROM OLD.stream_kind OR NEW.content_type IS DISTINCT FROM OLD.content_type
           OR NEW.content_encoding IS DISTINCT FROM OLD.content_encoding OR NEW.capture_source IS DISTINCT FROM OLD.capture_source
           OR NEW.schema_version IS DISTINCT FROM OLD.schema_version OR NEW.retention IS DISTINCT FROM OLD.retention
           OR NEW.expires_at IS DISTINCT FROM OLD.expires_at OR NEW.created_at IS DISTINCT FROM OLD.created_at
           OR NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
           OR NEW.capture_session_id IS DISTINCT FROM OLD.capture_session_id
           OR NEW.segment_count IS DISTINCT FROM OLD.segment_count OR NEW.total_bytes IS DISTINCT FROM OLD.total_bytes
           OR NEW.source_offset_bytes IS DISTINCT FROM OLD.source_offset_bytes
           OR NEW.capture_source_base_offset_bytes IS DISTINCT FROM OLD.capture_source_base_offset_bytes
           OR NEW.next_segment_ordinal IS DISTINCT FROM OLD.next_segment_ordinal
           OR NEW.next_offset_bytes IS DISTINCT FROM OLD.next_offset_bytes
           OR NEW.capture_finalized_at IS DISTINCT FROM OLD.capture_finalized_at
           OR NEW.completed_at IS DISTINCT FROM OLD.completed_at
           OR NEW.error_code IS DISTINCT FROM OLD.error_code OR NEW.error_message IS DISTINCT FROM OLD.error_message
           OR NEW.content_digest_algorithm IS DISTINCT FROM OLD.content_digest_algorithm
           OR NEW.content_digest IS DISTINCT FROM OLD.content_digest
           OR NEW.manifest_digest IS DISTINCT FROM OLD.manifest_digest
           OR NEW.remote_stall_since IS DISTINCT FROM OLD.remote_stall_since
           OR NEW.remote_stall_code IS DISTINCT FROM OLD.remote_stall_code THEN
            RAISE EXCEPTION 'agent_run_log_stream retention statement cannot rewrite anything but its own retention columns (id=%).', OLD.id;
        END IF;
        IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
            RAISE EXCEPTION 'agent_run_log_stream revision/time must advance monotonically (id=%, old_revision=%, new_revision=%).', OLD.id, OLD.revision, NEW.revision;
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
