-- 0135_agent_run_log_capture_intent_recovery.sql
--
-- Declares each exact native log source before stream open. A declared intent with no stream is therefore a durable
-- capture gap, while a finalized zero-byte stream is provably genuine no-output. Recovery is a bounded SKIP LOCKED
-- worker lease over this health-only ledger; it can complete/fail log streams but never mutates AgentRun lifecycle.

CREATE TABLE agent_run_log_capture_intent (
    id                         UUID          NOT NULL PRIMARY KEY,
    team_id                    UUID          NOT NULL,
    agent_run_id               UUID          NOT NULL,
    worker_fence_epoch         BIGINT        NOT NULL,
    capture_session_id         UUID          NOT NULL,
    stream_kind                VARCHAR(128)  NOT NULL,
    content_type               VARCHAR(255)  NOT NULL,
    content_encoding           VARCHAR(64)   NULL,
    capture_source             VARCHAR(128)  NOT NULL,
    stream_id                  UUID          NULL,
    state                      VARCHAR(32)   NOT NULL,
    revision                   BIGINT        NOT NULL,
    recovery_attempt_count     INTEGER       NOT NULL,
    recovery_started_at        TIMESTAMPTZ   NULL,
    next_recovery_at           TIMESTAMPTZ   NOT NULL,
    recovery_owner_id          UUID          NULL,
    recovery_fence_epoch       BIGINT        NOT NULL,
    recovery_lease_expires_at  TIMESTAMPTZ   NULL,
    last_error_code            VARCHAR(128)  NULL,
    last_error_message         VARCHAR(2048) NULL,
    created_at                 TIMESTAMPTZ   NOT NULL,
    last_modified_at           TIMESTAMPTZ   NOT NULL,
    terminal_observed_at       TIMESTAMPTZ   NULL,
    terminal_at                TIMESTAMPTZ   NULL,

    CONSTRAINT fk_agent_run_log_capture_intent_run FOREIGN KEY (team_id, agent_run_id)
        REFERENCES agent_run (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_agent_run_log_capture_intent_stream FOREIGN KEY (team_id, stream_id, agent_run_id)
        REFERENCES agent_run_log_stream (team_id, id, agent_run_id) ON DELETE RESTRICT,
    CONSTRAINT ck_agent_run_log_capture_intent_claim CHECK (
        recovery_fence_epoch >= 0 AND recovery_attempt_count >= 0
        AND ((recovery_attempt_count = 0 AND recovery_started_at IS NULL)
            OR (recovery_attempt_count > 0 AND recovery_started_at IS NOT NULL))
        AND ((recovery_owner_id IS NULL AND recovery_lease_expires_at IS NULL)
            OR (recovery_owner_id IS NOT NULL AND recovery_fence_epoch > 0 AND recovery_lease_expires_at IS NOT NULL))),
    CONSTRAINT ck_agent_run_log_capture_intent_error CHECK (
        (last_error_code IS NULL AND last_error_message IS NULL)
        OR (last_error_code IS NOT NULL AND btrim(last_error_code) <> '')),
    CONSTRAINT ck_agent_run_log_capture_intent_identity CHECK (
        worker_fence_epoch > 0 AND capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND stream_kind ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$'
        AND capture_source ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$'
        AND content_type ~ '^[^[:space:]/]+/[^[:space:]]+$'
        AND (content_encoding IS NULL OR content_encoding ~ '^[a-z0-9][a-z0-9._+-]{0,63}$')),
    CONSTRAINT ck_agent_run_log_capture_intent_state CHECK (
        state IN ('Expected', 'Opened', 'SourceFinalized', 'Completed', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate')
        AND ((state IN ('Completed', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate') AND terminal_at IS NOT NULL AND recovery_owner_id IS NULL)
            OR (state IN ('Expected', 'Opened', 'SourceFinalized') AND terminal_at IS NULL))
        AND (state IN ('Expected', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate') OR stream_id IS NOT NULL)),
    CONSTRAINT ck_agent_run_log_capture_intent_time CHECK (
        revision > 0 AND next_recovery_at >= created_at AND last_modified_at >= created_at
        AND (recovery_started_at IS NULL OR last_modified_at >= recovery_started_at)
        AND (terminal_observed_at IS NULL OR last_modified_at >= terminal_observed_at)
        AND (terminal_at IS NULL OR last_modified_at >= terminal_at))
);

CREATE UNIQUE INDEX ux_agent_run_log_capture_intent_identity
    ON agent_run_log_capture_intent (team_id, agent_run_id, worker_fence_epoch, capture_session_id, stream_kind);
CREATE INDEX ix_agent_run_log_capture_intent_recovery
    ON agent_run_log_capture_intent (next_recovery_at, team_id, id) INCLUDE (recovery_lease_expires_at, worker_fence_epoch)
    WHERE state IN ('Expected', 'Opened', 'SourceFinalized');
CREATE INDEX ix_agent_run_log_capture_intent_run
    ON agent_run_log_capture_intent (team_id, agent_run_id, created_at, id);

CREATE OR REPLACE FUNCTION agent_run_log_capture_intent_guard() RETURNS trigger AS $$
DECLARE
    current_fence BIGINT;
    current_status VARCHAR(16);
    linked_stream agent_run_log_stream%ROWTYPE;
    is_claim BOOLEAN := FALSE;
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

    IF NEW.recovery_fence_epoch IS DISTINCT FROM OLD.recovery_fence_epoch THEN
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
               OR NEW.last_error_code IS NULL THEN
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

CREATE TRIGGER agent_run_log_capture_intent_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON agent_run_log_capture_intent
    FOR EACH ROW EXECUTE FUNCTION agent_run_log_capture_intent_guard();

COMMENT ON TABLE agent_run_log_capture_intent IS
    'Expected AgentRun log sources declared before stream open; recovery mutates log health only, never AgentRun outcome.';
COMMENT ON COLUMN agent_run_log_capture_intent.worker_fence_epoch IS
    'Immutable AgentRun worker fence whose exact capture session declared this stream expectation.';
COMMENT ON COLUMN agent_run_log_capture_intent.recovery_fence_epoch IS
    'Independent monotonic reconciler claim fence; unrelated to AgentRun worker ownership.';
