-- 0137_workflow_run_harness_execution.sql
--
-- Durable EXECUTION IDENTITY for a harness run, plus one row per PHYSICAL process inside it. Today an AgentRun has
-- no notion of "which physical attempt produced this": revise rounds, re-attaches and worker replacements all fold
-- into the single mutable agent_run row, which is why a re-attach can only recover the tail, why a per-attempt cost
-- has nowhere to live, and why an execution on a non-local runner cannot be addressed at all.
--
-- OWNERSHIP. The execution is keyed to the AGENT RUN, not to a Workflow Run: agent_run.workflow_run_id is nullable
-- because an AgentRun is deliberately standalone-capable, so a NOT NULL workflow_run_id would make a standalone
-- execution unrepresentable. workflow_run_id is carried as the same nullable soft correlation agent_run itself uses
-- (no FK, matching the existing telemetry ledgers) and the guard proves it EQUALS the AgentRun's own value, so the
-- two can never disagree. The table names are the ones the data contract already registers, prefix included; the
-- prefix therefore names the contract's namespace here, not this table's aggregate root.
--
-- INVARIANTS, all enforced in the database rather than by a convention a writer may forget:
--   * one execution per (agent run, generation) — a unique index; generations are contiguous from one and a new one
--     cannot open while its predecessor is still live, so a double launch is unreachable.
--   * attempt ordinals contiguous from 1 within an execution — the parent holds the only ordinal the next attempt
--     may carry, proved under FOR UPDATE, and its AFTER-INSERT trigger advances the head in the same transaction.
--   * a terminal execution RELEASES its lease, and an attempt cannot be claimed once terminal — CHECK constraints,
--     so the illegal row is refused rather than merely never written.
--   * every attempt carries the EXACT current AgentRun worker fence, so a stale worker cannot append one.
--   * ACQUIRING the lease (or an attempt's observer claim) advances its fence by exactly one, and an acquisition over
--     a lease that is still live is refused — so each fence value is acquired by at most ONE owner. Both guards enter
--     that arm on EITHER axis (fence changed OR owner changed to somebody), following 0132's claim arm, because an
--     owner swap that leaves the fence untouched must not be a way around it.
--   * a lease/claim that is still live cannot be evicted by the same statement that closes the row — the terminal
--     arms refuse it, so satisfying ck_..._terminal_lease by nulling the lease in that statement no longer works.
--   * the runner locator is opaque and its KIND is recorded, so a container or remote runner is a new kind value
--     rather than a migration.
--
-- WHAT THIS SCHEMA DOES NOT PROVE, and therefore what every writer still owes. A row trigger sees only OLD and NEW,
-- never WHICH worker issued the statement, so holdership is not authenticated here:
--   * RELEASING a live lease (owner -> NULL, fence unchanged) is legal from any session, and so is any non-claim
--     write to a leased row. A writer proves it is the holder only by carrying its own predicate:
--     WHERE lease_owner_id = <me> AND lease_fence = <observed> AND revision = <observed>. Omit it and a third
--     party's release wins silently. This is what makes the fence useful: the displaced holder's own next write
--     fails its predicate instead of interleaving.
--   * a Pending generation whose launch died before its first attempt is closable ONLY as Abandoned with an
--     error_code (Exited requires attempt_count > 0), and the lease-expiry index cannot see it because its
--     lease_expires_at is NULL by birth. Something must age-scan ix_..._stale_live and Abandon it, or its AgentRun
--     can never open another generation — the generation gate refuses to open one over a live predecessor.
--
-- State on both tables describes the PROCESS lifecycle, never the task's verdict — agent_run.status remains the only
-- outcome authority, and nothing reads or writes these tables in this slice.
-- Rollback: DROP TABLE workflow_run_harness_process_attempt; DROP TABLE workflow_run_harness_execution;

CREATE TABLE workflow_run_harness_execution (
    id                             UUID          NOT NULL PRIMARY KEY,
    team_id                        UUID          NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    agent_run_id                   UUID          NOT NULL,
    workflow_run_id                UUID          NULL,
    generation                     INTEGER       NOT NULL,
    harness_type_key               VARCHAR(160)  NOT NULL,
    runner_kind                    VARCHAR(64)   NOT NULL,
    runner_locator_schema_version  INTEGER       NOT NULL,
    runner_host_affinity           VARCHAR(255)  NULL,
    deadline_at                    TIMESTAMPTZ   NULL,
    state                          VARCHAR(24)   NOT NULL,
    attempt_count                  INTEGER       NOT NULL,
    next_attempt_ordinal           INTEGER       NOT NULL,
    lease_owner_id                 UUID          NULL,
    lease_fence                    BIGINT        NOT NULL,
    lease_expires_at               TIMESTAMPTZ   NULL,
    revision                       BIGINT        NOT NULL,
    created_at                     TIMESTAMPTZ   NOT NULL,
    last_modified_at               TIMESTAMPTZ   NOT NULL,
    terminal_at                    TIMESTAMPTZ   NULL,
    error_code                     VARCHAR(128)  NULL,
    error_message                  VARCHAR(2048) NULL,

    CONSTRAINT ak_workflow_run_harness_execution_scope UNIQUE (team_id, id, agent_run_id),
    CONSTRAINT fk_workflow_run_harness_execution_agent_run FOREIGN KEY (team_id, agent_run_id)
        REFERENCES agent_run (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_harness_execution_error CHECK (
        (error_code IS NULL AND error_message IS NULL)
        OR (error_code IS NOT NULL AND btrim(error_code) <> '')),
    CONSTRAINT ck_workflow_run_harness_execution_head CHECK (
        generation > 0 AND attempt_count >= 0 AND next_attempt_ordinal = attempt_count + 1
        AND runner_locator_schema_version > 0 AND revision > 0),
    CONSTRAINT ck_workflow_run_harness_execution_identity CHECK (
        harness_type_key ~ '^[a-z0-9][a-z0-9._-]{0,126}/v[1-9][0-9]*$'
        AND runner_kind ~ '^[a-z0-9][a-z0-9._-]{0,63}$'
        AND (runner_host_affinity IS NULL OR btrim(runner_host_affinity) <> '')),
    CONSTRAINT ck_workflow_run_harness_execution_lease CHECK (
        lease_fence >= 0
        AND ((lease_owner_id IS NULL AND lease_expires_at IS NULL)
            OR (lease_owner_id IS NOT NULL AND lease_fence > 0 AND lease_expires_at IS NOT NULL))),
    CONSTRAINT ck_workflow_run_harness_execution_state CHECK (
        state IN ('Pending', 'Running', 'Exited', 'Abandoned')),
    CONSTRAINT ck_workflow_run_harness_execution_terminal CHECK (
        (state IN ('Pending', 'Running') AND terminal_at IS NULL AND error_code IS NULL)
        OR (state = 'Exited' AND terminal_at IS NOT NULL AND attempt_count > 0)
        OR (state = 'Abandoned' AND terminal_at IS NOT NULL AND error_code IS NOT NULL)),
    CONSTRAINT ck_workflow_run_harness_execution_terminal_lease CHECK (
        state IN ('Pending', 'Running') OR (lease_owner_id IS NULL AND lease_expires_at IS NULL)),
    CONSTRAINT ck_workflow_run_harness_execution_time CHECK (
        last_modified_at >= created_at
        AND (terminal_at IS NULL OR (terminal_at >= created_at AND last_modified_at >= terminal_at))
        AND (deadline_at IS NULL OR deadline_at > created_at))
);

CREATE UNIQUE INDEX ux_workflow_run_harness_execution_generation
    ON workflow_run_harness_execution (team_id, agent_run_id, generation);
CREATE INDEX ix_workflow_run_harness_execution_state_modified
    ON workflow_run_harness_execution (team_id, state, last_modified_at, id);
CREATE INDEX ix_workflow_run_harness_execution_lease_expiry
    ON workflow_run_harness_execution (lease_expires_at, team_id, id)
    WHERE state IN ('Pending', 'Running');
-- The AGE scan a stale-execution reaper needs. ix_..._lease_expiry cannot serve it: a generation whose launch died
-- before its first attempt has lease_expires_at NULL by birth, so an expiry predicate never returns it, and it then
-- blocks its AgentRun's every future generation. Leading on last_modified_at (no team prefix) so one reaper sweeps
-- every tenant, matching idx_agent_run_running_heartbeat.
CREATE INDEX ix_workflow_run_harness_execution_stale_live
    ON workflow_run_harness_execution (last_modified_at, team_id, id)
    WHERE state IN ('Pending', 'Running');
CREATE INDEX ix_workflow_run_harness_execution_workflow_run
    ON workflow_run_harness_execution (team_id, workflow_run_id, created_at, id)
    WHERE workflow_run_id IS NOT NULL;

CREATE TABLE workflow_run_harness_process_attempt (
    id                       UUID          NOT NULL PRIMARY KEY,
    team_id                  UUID          NOT NULL,
    agent_run_id             UUID          NOT NULL,
    execution_id             UUID          NOT NULL,
    attempt_ordinal          INTEGER       NOT NULL,
    worker_fence_epoch       BIGINT        NOT NULL,
    runner_locator_jsonb     JSONB         NOT NULL,
    remote_execution_id      VARCHAR(512)  NULL,
    checkpoint_ref           VARCHAR(1024) NULL,
    state                    VARCHAR(24)   NOT NULL,
    exit_code                INTEGER       NULL,
    claim_owner_id           UUID          NULL,
    claim_fence              BIGINT        NOT NULL,
    claim_expires_at         TIMESTAMPTZ   NULL,
    revision                 BIGINT        NOT NULL,
    started_at               TIMESTAMPTZ   NOT NULL,
    last_observed_at         TIMESTAMPTZ   NOT NULL,
    exited_at                TIMESTAMPTZ   NULL,
    created_at               TIMESTAMPTZ   NOT NULL,
    last_modified_at         TIMESTAMPTZ   NOT NULL,
    error_code               VARCHAR(128)  NULL,
    error_message            VARCHAR(2048) NULL,

    CONSTRAINT fk_workflow_run_harness_process_attempt_execution FOREIGN KEY (team_id, execution_id, agent_run_id)
        REFERENCES workflow_run_harness_execution (team_id, id, agent_run_id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_harness_process_attempt_bounds CHECK (
        attempt_ordinal > 0 AND worker_fence_epoch > 0 AND claim_fence >= 0 AND revision > 0),
    CONSTRAINT ck_workflow_run_harness_process_attempt_claim CHECK (
        (claim_owner_id IS NULL AND claim_expires_at IS NULL)
        OR (claim_owner_id IS NOT NULL AND claim_fence > 0 AND claim_expires_at IS NOT NULL)),
    CONSTRAINT ck_workflow_run_harness_process_attempt_error CHECK (
        (error_code IS NULL AND error_message IS NULL)
        OR (error_code IS NOT NULL AND btrim(error_code) <> '')),
    CONSTRAINT ck_workflow_run_harness_process_attempt_locator CHECK (
        jsonb_typeof(runner_locator_jsonb) = 'object'
        AND (remote_execution_id IS NULL OR btrim(remote_execution_id) <> '')
        AND (checkpoint_ref IS NULL OR btrim(checkpoint_ref) <> '')),
    CONSTRAINT ck_workflow_run_harness_process_attempt_state CHECK (
        state IN ('Running', 'Exited', 'Lost')),
    CONSTRAINT ck_workflow_run_harness_process_attempt_terminal CHECK (
        (state = 'Running' AND exited_at IS NULL AND exit_code IS NULL AND error_code IS NULL)
        OR (state = 'Exited' AND exited_at IS NOT NULL AND exit_code IS NOT NULL)
        OR (state = 'Lost' AND exited_at IS NOT NULL AND exit_code IS NULL AND error_code IS NOT NULL)),
    CONSTRAINT ck_workflow_run_harness_process_attempt_terminal_claim CHECK (
        state = 'Running' OR (claim_owner_id IS NULL AND claim_expires_at IS NULL)),
    CONSTRAINT ck_workflow_run_harness_process_attempt_time CHECK (
        created_at >= started_at AND last_observed_at >= started_at AND last_modified_at >= created_at
        AND (exited_at IS NULL OR (exited_at >= started_at AND last_observed_at >= exited_at)))
);

CREATE UNIQUE INDEX ux_workflow_run_harness_process_attempt_ordinal
    ON workflow_run_harness_process_attempt (team_id, execution_id, attempt_ordinal);
CREATE INDEX ix_workflow_run_harness_process_attempt_run_started
    ON workflow_run_harness_process_attempt (team_id, agent_run_id, started_at, id);
CREATE INDEX ix_workflow_run_harness_process_attempt_live_claim
    ON workflow_run_harness_process_attempt (claim_expires_at, team_id, id)
    WHERE state = 'Running';

CREATE OR REPLACE FUNCTION workflow_run_harness_execution_guard() RETURNS trigger AS $$
DECLARE
    run_workflow_run_id UUID;
    previous workflow_run_harness_execution%ROWTYPE;
    appended workflow_run_harness_process_attempt%ROWTYPE;
    live_attempt_id UUID;
    is_lease_claim BOOLEAN := FALSE;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'workflow_run_harness_execution is durable execution identity — DELETE rejected (id=%).', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        SELECT workflow_run_id INTO run_workflow_run_id FROM agent_run
        WHERE team_id = NEW.team_id AND id = NEW.agent_run_id
        FOR SHARE;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'workflow_run_harness_execution requires its tenant-bound AgentRun (run_id=%).', NEW.agent_run_id;
        END IF;
        IF NEW.workflow_run_id IS DISTINCT FROM run_workflow_run_id THEN
            RAISE EXCEPTION 'workflow_run_harness_execution must mirror its AgentRun workflow run exactly (run_id=%, attempted=%, actual=%).', NEW.agent_run_id, NEW.workflow_run_id, run_workflow_run_id;
        END IF;
        IF NEW.state <> 'Pending' OR NEW.revision <> 1 OR NEW.attempt_count <> 0 OR NEW.next_attempt_ordinal <> 1
           OR NEW.lease_fence <> 0 OR NEW.lease_owner_id IS NOT NULL OR NEW.lease_expires_at IS NOT NULL
           OR NEW.terminal_at IS NOT NULL OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL
           OR NEW.last_modified_at IS DISTINCT FROM NEW.created_at THEN
            RAISE EXCEPTION 'workflow_run_harness_execution must start as an unclaimed empty Pending revision-one generation (id=%).', NEW.id;
        END IF;

        SELECT * INTO previous FROM workflow_run_harness_execution
        WHERE team_id = NEW.team_id AND agent_run_id = NEW.agent_run_id
        ORDER BY generation DESC
        LIMIT 1
        FOR UPDATE;
        IF NOT FOUND THEN
            IF NEW.generation <> 1 THEN
                RAISE EXCEPTION 'workflow_run_harness_execution generations are contiguous from one (run_id=%, attempted=%).', NEW.agent_run_id, NEW.generation;
            END IF;
            RETURN NEW;
        END IF;
        IF NEW.generation <> previous.generation + 1 THEN
            RAISE EXCEPTION 'workflow_run_harness_execution generations are contiguous from one (run_id=%, attempted=%, previous=%).', NEW.agent_run_id, NEW.generation, previous.generation;
        END IF;
        IF previous.state IN ('Pending', 'Running') THEN
            RAISE EXCEPTION 'workflow_run_harness_execution cannot open a generation while its predecessor is live (run_id=%, previous_generation=%, previous_state=%).', NEW.agent_run_id, previous.generation, previous.state;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.agent_run_id IS DISTINCT FROM OLD.agent_run_id OR NEW.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id
       OR NEW.generation IS DISTINCT FROM OLD.generation OR NEW.harness_type_key IS DISTINCT FROM OLD.harness_type_key
       OR NEW.runner_kind IS DISTINCT FROM OLD.runner_kind
       OR NEW.runner_locator_schema_version IS DISTINCT FROM OLD.runner_locator_schema_version
       OR NEW.runner_host_affinity IS DISTINCT FROM OLD.runner_host_affinity
       OR NEW.deadline_at IS DISTINCT FROM OLD.deadline_at OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'workflow_run_harness_execution stable execution identity is immutable (id=%).', OLD.id;
    END IF;
    IF OLD.state IN ('Exited', 'Abandoned') THEN
        RAISE EXCEPTION 'workflow_run_harness_execution terminal state is immutable (id=%, state=%).', OLD.id, OLD.state;
    END IF;
    IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
        RAISE EXCEPTION 'workflow_run_harness_execution revision must advance exactly once and time must not rewind (id=%, old_revision=%, new_revision=%).', OLD.id, OLD.revision, NEW.revision;
    END IF;

    -- EITHER axis is a claim, as 0132's arm is: an owner swap that leaves the fence untouched is exactly how a live
    -- lease would otherwise be taken. Owner -> NULL is a RELEASE, not a claim, and stays outside so the terminal path
    -- and a graceful hand-back still work; a same-owner expiry renewal touches neither axis and stays outside too.
    IF NEW.lease_fence IS DISTINCT FROM OLD.lease_fence
       OR (NEW.lease_owner_id IS DISTINCT FROM OLD.lease_owner_id AND NEW.lease_owner_id IS NOT NULL) THEN
        IF OLD.lease_expires_at > clock_timestamp() THEN
            RAISE EXCEPTION 'workflow_run_harness_execution live lease cannot be reclaimed (id=%, fence=%, holder=%).', OLD.id, OLD.lease_fence, OLD.lease_owner_id;
        END IF;
        IF NEW.lease_fence <> OLD.lease_fence + 1 OR NEW.lease_owner_id IS NULL
           OR NEW.lease_expires_at IS NULL OR NEW.lease_expires_at <= clock_timestamp() THEN
            RAISE EXCEPTION 'workflow_run_harness_execution lease claim must advance the fence exactly once with a live expiry (id=%, old=%, attempted=%).', OLD.id, OLD.lease_fence, NEW.lease_fence;
        END IF;
        IF NEW.state IS DISTINCT FROM OLD.state OR NEW.attempt_count IS DISTINCT FROM OLD.attempt_count
           OR NEW.next_attempt_ordinal IS DISTINCT FROM OLD.next_attempt_ordinal
           OR NEW.terminal_at IS DISTINCT FROM OLD.terminal_at
           OR NEW.error_code IS DISTINCT FROM OLD.error_code OR NEW.error_message IS DISTINCT FROM OLD.error_message THEN
            RAISE EXCEPTION 'workflow_run_harness_execution lease claim cannot mutate execution state (id=%).', OLD.id;
        END IF;
        is_lease_claim := TRUE;
    END IF;

    IF NOT is_lease_claim AND NEW.attempt_count IS DISTINCT FROM OLD.attempt_count THEN
        IF NEW.attempt_count <> OLD.attempt_count + 1 OR NEW.next_attempt_ordinal <> OLD.next_attempt_ordinal + 1
           OR NEW.state <> 'Running' OR NEW.terminal_at IS NOT NULL
           OR NEW.lease_owner_id IS DISTINCT FROM OLD.lease_owner_id
           OR NEW.lease_expires_at IS DISTINCT FROM OLD.lease_expires_at THEN
            RAISE EXCEPTION 'workflow_run_harness_execution attempt-head advances are exactly one live attempt (id=%).', OLD.id;
        END IF;

        SELECT * INTO appended FROM workflow_run_harness_process_attempt
        WHERE team_id = NEW.team_id AND execution_id = NEW.id AND agent_run_id = NEW.agent_run_id
          AND attempt_ordinal = OLD.next_attempt_ordinal;
        IF NOT FOUND OR appended.created_at > NEW.last_modified_at THEN
            RAISE EXCEPTION 'workflow_run_harness_execution head advance requires its exact appended attempt (id=%, ordinal=%).', OLD.id, OLD.next_attempt_ordinal;
        END IF;
        RETURN NEW;
    END IF;

    IF NOT is_lease_claim AND NEW.state IS DISTINCT FROM OLD.state THEN
        IF NEW.state NOT IN ('Exited', 'Abandoned') OR NEW.terminal_at IS NULL OR NEW.terminal_at < OLD.created_at THEN
            RAISE EXCEPTION 'workflow_run_harness_execution illegal state transition (id=%, old=%, new=%).', OLD.id, OLD.state, NEW.state;
        END IF;
        -- ck_..._terminal_lease alone is satisfiable by nulling the lease in the very statement that closes the row,
        -- which is how a third party evicted a live holder and froze the execution with its own error_code. A live
        -- lease must lapse or be released FIRST, as its own revision the displaced holder's predicate can detect.
        IF OLD.lease_expires_at > clock_timestamp() AND NEW.lease_owner_id IS NULL THEN
            RAISE EXCEPTION 'workflow_run_harness_execution live lease must be released before its execution is closed (id=%, holder=%, fence=%).', OLD.id, OLD.lease_owner_id, OLD.lease_fence;
        END IF;

        SELECT id INTO live_attempt_id FROM workflow_run_harness_process_attempt
        WHERE team_id = OLD.team_id AND execution_id = OLD.id AND agent_run_id = OLD.agent_run_id
          AND state = 'Running'
        ORDER BY attempt_ordinal
        LIMIT 1
        FOR SHARE;
        IF FOUND THEN
            RAISE EXCEPTION 'workflow_run_harness_execution cannot terminalize while an attempt is still running (id=%, attempt_id=%).', OLD.id, live_attempt_id;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_harness_execution_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_harness_execution
    FOR EACH ROW EXECUTE FUNCTION workflow_run_harness_execution_guard();

CREATE OR REPLACE FUNCTION workflow_run_harness_process_attempt_guard() RETURNS trigger AS $$
DECLARE
    current_fence BIGINT;
    execution workflow_run_harness_execution%ROWTYPE;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'workflow_run_harness_process_attempt is durable process identity — DELETE rejected (id=%).', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        SELECT fence_epoch INTO current_fence FROM agent_run
        WHERE team_id = NEW.team_id AND id = NEW.agent_run_id
        FOR SHARE;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt requires its tenant-bound AgentRun (run_id=%).', NEW.agent_run_id;
        END IF;
        IF current_fence <= 0 OR NEW.worker_fence_epoch <> current_fence THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt stale worker fence rejected (run_id=%, current=%, attempted=%).', NEW.agent_run_id, current_fence, NEW.worker_fence_epoch;
        END IF;

        SELECT * INTO execution FROM workflow_run_harness_execution
        WHERE team_id = NEW.team_id AND id = NEW.execution_id AND agent_run_id = NEW.agent_run_id
        FOR UPDATE;
        IF NOT FOUND OR execution.state NOT IN ('Pending', 'Running') THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt requires its live tenant-bound execution (execution_id=%).', NEW.execution_id;
        END IF;
        IF NEW.attempt_ordinal <> execution.next_attempt_ordinal THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt ordinals are contiguous from one (execution_id=%, expected=%, attempted=%).', NEW.execution_id, execution.next_attempt_ordinal, NEW.attempt_ordinal;
        END IF;
        IF NEW.state <> 'Running' OR NEW.revision <> 1 OR NEW.exit_code IS NOT NULL OR NEW.exited_at IS NOT NULL
           OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL OR NEW.claim_fence <> 0
           OR NEW.claim_owner_id IS NOT NULL OR NEW.claim_expires_at IS NOT NULL
           OR NEW.last_modified_at IS DISTINCT FROM NEW.created_at THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt must start as an unclaimed Running revision-one process (id=%).', NEW.id;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.agent_run_id IS DISTINCT FROM OLD.agent_run_id OR NEW.execution_id IS DISTINCT FROM OLD.execution_id
       OR NEW.attempt_ordinal IS DISTINCT FROM OLD.attempt_ordinal
       OR NEW.worker_fence_epoch IS DISTINCT FROM OLD.worker_fence_epoch
       OR NEW.runner_locator_jsonb IS DISTINCT FROM OLD.runner_locator_jsonb
       OR NEW.remote_execution_id IS DISTINCT FROM OLD.remote_execution_id
       OR NEW.started_at IS DISTINCT FROM OLD.started_at OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'workflow_run_harness_process_attempt stable process identity is immutable (id=%).', OLD.id;
    END IF;
    IF OLD.state <> 'Running' THEN
        RAISE EXCEPTION 'workflow_run_harness_process_attempt terminal state is immutable (id=%, state=%).', OLD.id, OLD.state;
    END IF;
    IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at
       OR NEW.last_observed_at < OLD.last_observed_at THEN
        RAISE EXCEPTION 'workflow_run_harness_process_attempt revision/observation must advance monotonically (id=%, old_revision=%, new_revision=%).', OLD.id, OLD.revision, NEW.revision;
    END IF;

    -- Same EITHER-axis entry as the execution's lease arm and 0132's: an observer swap that leaves claim_fence alone
    -- is exactly how a live claim would otherwise be stolen. Owner -> NULL is a release and stays outside the arm.
    IF NEW.claim_fence IS DISTINCT FROM OLD.claim_fence
       OR (NEW.claim_owner_id IS DISTINCT FROM OLD.claim_owner_id AND NEW.claim_owner_id IS NOT NULL) THEN
        IF NEW.state <> 'Running' THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt cannot be claimed once terminal (id=%, attempted_state=%).', OLD.id, NEW.state;
        END IF;
        IF OLD.claim_expires_at > clock_timestamp() THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt live claim cannot be stolen (id=%, fence=%, holder=%).', OLD.id, OLD.claim_fence, OLD.claim_owner_id;
        END IF;
        IF NEW.claim_fence <> OLD.claim_fence + 1 OR NEW.claim_owner_id IS NULL
           OR NEW.claim_expires_at IS NULL OR NEW.claim_expires_at <= clock_timestamp() THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt claim must advance the fence exactly once with a live expiry (id=%, old=%, attempted=%).', OLD.id, OLD.claim_fence, NEW.claim_fence;
        END IF;
        IF NEW.exit_code IS DISTINCT FROM OLD.exit_code OR NEW.exited_at IS DISTINCT FROM OLD.exited_at
           OR NEW.checkpoint_ref IS DISTINCT FROM OLD.checkpoint_ref
           OR NEW.error_code IS DISTINCT FROM OLD.error_code OR NEW.error_message IS DISTINCT FROM OLD.error_message THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt claim cannot mutate observed process state (id=%).', OLD.id;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.state IS DISTINCT FROM OLD.state THEN
        IF NEW.state NOT IN ('Exited', 'Lost') OR NEW.exited_at IS NULL OR NEW.exited_at < OLD.started_at THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt illegal process outcome (id=%, old=%, new=%).', OLD.id, OLD.state, NEW.state;
        END IF;
        -- The resurrected-observer path: a worker whose AgentRun fence was superseded wrote Lost plus its own reason
        -- over the live claim of the observer that replaced it, and the frozen terminal state then let anyone close
        -- the parent execution. Note this fences on the CLAIM, not on worker_fence_epoch as 0132's terminal arm does:
        -- that column is immutable here by design (it records the fence that LAUNCHED the process), so requiring it to
        -- equal the AgentRun's current fence would make every attempt permanently unclosable after any fence bump.
        IF OLD.claim_expires_at > clock_timestamp() AND NEW.claim_owner_id IS NULL THEN
            RAISE EXCEPTION 'workflow_run_harness_process_attempt live claim must be released before its process outcome is recorded (id=%, holder=%, fence=%).', OLD.id, OLD.claim_owner_id, OLD.claim_fence;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_harness_process_attempt_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_harness_process_attempt
    FOR EACH ROW EXECUTE FUNCTION workflow_run_harness_process_attempt_guard();

CREATE OR REPLACE FUNCTION workflow_run_harness_process_attempt_advance_head() RETURNS trigger AS $$
BEGIN
    UPDATE workflow_run_harness_execution SET
        revision = revision + 1,
        state = 'Running',
        attempt_count = attempt_count + 1,
        next_attempt_ordinal = next_attempt_ordinal + 1,
        last_modified_at = GREATEST(last_modified_at, NEW.created_at)
    WHERE team_id = NEW.team_id AND id = NEW.execution_id AND agent_run_id = NEW.agent_run_id;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_harness_process_attempt_advance_execution_head
    AFTER INSERT ON workflow_run_harness_process_attempt
    FOR EACH ROW EXECUTE FUNCTION workflow_run_harness_process_attempt_advance_head();

COMMENT ON TABLE workflow_run_harness_execution IS
    'One logical harness execution of an AgentRun, one row per generation. State is process lifecycle, never the task outcome.';
COMMENT ON TABLE workflow_run_harness_process_attempt IS
    'One physical harness process inside an execution. Ordinals are contiguous from one; the runner locator is opaque to shared code.';
COMMENT ON COLUMN workflow_run_harness_execution.agent_run_id IS
    'Owning AgentRun. Execution identity is AgentRun-keyed because an AgentRun may be standalone with no workflow run.';
COMMENT ON COLUMN workflow_run_harness_execution.generation IS
    'One-based contiguous supersession counter; a re-launch opens the next generation, a re-attach only raises the lease fence.';
COMMENT ON COLUMN workflow_run_harness_execution.state IS
    'Process lifecycle. A Pending generation with attempt_count 0 (a launch that died before its first attempt) can be closed ONLY as Abandoned with an error_code, and until it is, no later generation of its AgentRun may open. Age-scan ix_workflow_run_harness_execution_stale_live to find it: its lease_expires_at is NULL, so the lease-expiry index never returns it.';
COMMENT ON COLUMN workflow_run_harness_execution.lease_fence IS
    'Advances by exactly one on every ACQUISITION, and an acquisition over a still-live lease is refused, so each value is acquired by at most one owner. A release leaves it where it is. The trigger cannot tell who issued a statement: a holder proves itself with WHERE lease_owner_id = <me> AND lease_fence = <observed> AND revision = <observed>.';
COMMENT ON COLUMN workflow_run_harness_execution.runner_kind IS
    'Runner backend that owns this execution and the only interpreter of its attempts opaque locators.';
COMMENT ON COLUMN workflow_run_harness_process_attempt.worker_fence_epoch IS
    'Immutable exact AgentRun worker fence that launched this process; distinct from the observer claim fence. Immutable by design, so it is NOT the axis the terminal arm fences on — the live observer claim is.';
COMMENT ON COLUMN workflow_run_harness_process_attempt.claim_fence IS
    'Observer claim fence with the same rule as the execution lease: exactly one step per acquisition, no acquisition over a live claim, unchanged by a release. Holdership is the writer predicate WHERE claim_owner_id = <me> AND claim_fence = <observed> AND revision = <observed>, never something the trigger can infer.';
COMMENT ON COLUMN workflow_run_harness_process_attempt.runner_locator_jsonb IS
    'Backend-owned opaque address for this process (local pid/spool, container id, remote reference). Never interpreted by shared code.';
