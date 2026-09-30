-- 0240_capture_intent_retry_due_after_its_settlement.sql
--
-- A correct recovery settlement was refused whenever it took longer than the retry it scheduled.
--
-- AgentRunLogCaptureRecoveryService.SettleAsync reads the DB clock once, decides the outcome at that instant and
-- schedules a retry as next_recovery_at = that instant + delay. It then reads the manifest stream and its verification
-- progress, and only then writes. The guard below checked the retry against a SECOND clock read, taken inside the
-- write, in both of its retry clauses:
--
--     OR (NEW.last_error_code <> 'terminal-grace-armed' AND NEW.next_recovery_at <= clock_timestamp())
--     OR (NEW.last_error_code IS NULL AND NOT (is_manifest AND ... AND NEW.next_recovery_at > clock_timestamp()))
--
-- so a settlement whose reads outlasted its own delay was refused as "no future retry". The service counts a refused
-- settlement as a lost lease: the intent sat claimed and idle until that lease expired, and the re-claim raised its
-- recovery_attempt_count once more.
--
-- With the production defaults only the second clause, the 1 s verification-progress retry, can be overtaken: a
-- backoff retry is due at least 52.5 s out, and a settlement is cancelled after 5 s. A progress settlement that
-- stalled for 1 s or more after its clock read lost its progress write, left the intent idle under its 30 s lease,
-- and roughly doubled each later backoff delay, up to its cap. It cost no attempt against the exhaustion budget:
-- v3 recovery counts stalled verification attempts, which a refused write never increments, and the re-claim's
-- settlement records the durable checkpoint. A test configuration with a 10 ms backoff is exposed to the first
-- clause as well. There the first settlement in a process, which pays for EF query compilation between the two
-- reads, is slow enough, and an intent allowed two stalled attempts was claimed three times.
--
-- The retry is now checked against the settling transaction's start, which precedes every clock read the settlement
-- makes. That is the instant the recovery queue relies on: every settled row is terminal or due strictly beyond the
-- frozen cutoff of the wave that claimed it, and that cutoff was read before the claim, which committed before this
-- transaction began. A retry due at or before its settlement began is still refused. The lease checks keep
-- clock_timestamp(): a lease must still be live when the write lands, however late that is.
--
-- The function body is 0201's, unchanged apart from the two retry comparisons.

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
               OR (NEW.last_error_code <> 'terminal-grace-armed' AND NEW.next_recovery_at <= transaction_timestamp())
               OR (NEW.last_error_code IS NULL AND NOT (is_manifest AND NEW.verification_progress_ordinal > OLD.verification_progress_ordinal AND NEW.next_recovery_at > transaction_timestamp())) THEN
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
