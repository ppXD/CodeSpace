-- 0141_workflow_run_tool_call.sql
--
-- The TOOL CALL data plane, schema only: workflow_run_tool_call is the LOGICAL invocation — which tool, declared by
-- whom, read-only or side-effecting, with which canonical arguments — and workflow_run_tool_call_attempt is each
-- PHYSICAL try of it. The split is 0124's, deliberately, because the question is 0124's: "did this retry, and what
-- did each try actually do" has one answer shape, so a retry appends an attempt instead of overwriting the transport,
-- result, outcome or timing of the try before it.
--
-- WHAT THIS ANSWERS THAT NOTHING TODAY CAN. Tool activity is recorded as untyped ledger noise plus, where a hash is
-- kept at all, an INPUT hash — so "which tool did what, with what arguments, to what effect, and did it retry" is not
-- a query. Here each of those five is a column or a reference: tool_kind/tool_namespace/tool_name is identity,
-- effect_class is whether it could mutate anything, arguments_artifact_id and result_artifact_id are the content, the
-- attempt's status/started_at/completed_at is the outcome and its cost in wall clock, and attempt_ordinal plus
-- retry_of_attempt_id is the retry lineage.
--
-- RELATIONSHIP TO tool_call_ledger, WHICH THIS DOES NOT REPLACE. The ledger (0049-0061) owns GOVERNANCE and
-- EXACTLY-ONCE: ux_tool_call_ledger_run_key is the single dedup authority for a side-effecting call, and the
-- ToolCallLedgerStateMachine CAS is the single-winner execution claim. This plane owns OBSERVATION, and it is
-- deliberately powerless: no unique key here dedups a side effect, and no state here gates one. Where a row IS a
-- projection of a ledger row, source_kind is 'tool-call-ledger/v1' and source_correlation_id is that ledger row's id,
-- and ux_workflow_run_tool_call_source_identity dedups the PROJECTION — never the invocation. Two mechanisms both
-- believing they own exactly-once is worse than one, so this one is stated to own none of it.
--
-- REDACTION, AND EXACTLY WHAT IT GUARANTEES. A tool argument can carry a credential, so no unbounded content lives in
-- these rows at all: arguments and results are artifact references, following 0129/0133's discipline that the durable
-- bytes are the REDACTED ones and the raw source survives only as a cursor. Each content axis is a single THREE-ARM
-- state, exhaustive and mutually exclusive, so there is no fourth combination for a writer to land in:
--   * NOTHING STATED — no artifact, no redaction, no policy, and a completeness that is not Exact/RedactedExact. This
--     is the honest birth state: redaction is a property of captured bytes, so with no bytes there is none to claim,
--     which is why these columns are nullable rather than defaulted to a reassuring value.
--   * WITHHELD — deliberately not captured: no artifact, and completeness exactly Unavailable.
--   * CAPTURED (None or Masked) — an artifact reference, a canonical sha256 digest of the bytes THAT reference points
--     at (each referenced payload carries its own: result_digest for the result, error_digest for the error body), and
--     redaction_policy NAMING the pass that cleared them. Masked bytes may never claim Exact completeness.
-- The last two rules are the ones NativeRecordV1 validates in process, restated here so the two spellings cannot
-- drift. Together they mean a writer that skipped redaction has no legal row to write: its INSERT fails rather than
-- quietly succeeding, and absent content can never be read as empty content because an Exact or RedactedExact claim
-- requires the reference. Once an axis is STATED it is immutable — a fill happens once, and a Withheld decision is
-- never quietly upgraded into bytes.
-- What this does NOT enforce, and no schema can: that a writer claiming redaction 'None' actually ran the redactor.
-- The database can refuse an unredacted-BY-DECLARATION row; it cannot inspect the bytes. That residue is code review's.
--
-- KEYING is 0124's plus 0130's run foreign key, so the two planes join on identical scope columns and one reader can
-- ask both the same question. workflow_run_id is therefore NOT NULL here: a tool call made by a STANDALONE AgentRun
-- (agent_run.workflow_run_id IS NULL) has no row in this plane, which is a named gap rather than an oversight — that
-- case belongs to the AgentRun-keyed harness execution plane 0137 built, and giving it a home here would mean a
-- second nullable identity axis the attempt's composite scope proof could not use. model_call_id is a SOFT reference
-- for the same reason artifact ids are: model-call rows arrive by a bounded sweeper, so a foreign key would refuse a
-- tool call whose causing model call has not been projected yet.
--
-- Time and lifecycle naming follows 0137 (created_at / last_modified_at / terminal_at / revision) rather than 0124's
-- IAuditable pair, because this plane enforces a state machine and a half-auditable row reads worse than either.
-- state describes the INVOCATION lifecycle only — never the task's verdict, never a completion or terminal decision,
-- which agent_run.status and the existing AgentRunResult keep entirely. DELETE is permitted at the CALL level and
-- cascades to its attempts, matching 0124 and the retention discipline #1489 set: a plane with many rows per run must
-- stay prunable. Deleting an attempt on its own is refused, because attempt_count and next_attempt_ordinal are a
-- DERIVED head no DELETE updates — the call would go on naming a try that no longer exists, and close claiming it.
--
-- Nothing in this slice produces, reads, folds or bills a row in either table.
-- Rollback: DROP TABLE workflow_run_tool_call_attempt; DROP TABLE workflow_run_tool_call;

CREATE TABLE workflow_run_tool_call (
    id                          UUID          NOT NULL PRIMARY KEY,
    team_id                     UUID          NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    workflow_run_id             UUID          NOT NULL,
    node_id                     VARCHAR(256)  NULL,
    iteration_key               VARCHAR(1024) NOT NULL DEFAULT '',
    work_plan_id                UUID          NULL,
    plan_version                INTEGER       NULL,
    work_unit_id                VARCHAR(512)  NULL,
    work_unit_contract_hash     VARCHAR(128)  NULL,
    execution_attempt_id        UUID          NULL,
    execution_attempt_ordinal   INTEGER       NULL,
    execution_generation        INTEGER       NULL,
    call_ordinal                BIGINT        NOT NULL,
    model_call_id               UUID          NULL,
    purpose                     VARCHAR(128)  NOT NULL,
    tool_kind                   VARCHAR(160)  NOT NULL,
    tool_namespace              VARCHAR(200)  NULL,
    tool_name                   VARCHAR(200)  NOT NULL,
    effect_class                VARCHAR(16)   NOT NULL,
    arguments_artifact_id       UUID          NULL,
    arguments_digest            VARCHAR(64)   NULL,
    arguments_redaction         VARCHAR(16)   NULL,
    redaction_policy            VARCHAR(200)  NULL,
    source_kind                 VARCHAR(64)   NULL,
    source_correlation_id       UUID          NULL,
    capture_source              VARCHAR(64)   NOT NULL,
    capture_completeness        VARCHAR(20)   NOT NULL,
    state                       VARCHAR(24)   NOT NULL,
    attempt_count               INTEGER       NOT NULL,
    next_attempt_ordinal        INTEGER       NOT NULL,
    revision                    BIGINT        NOT NULL,
    schema_version              INTEGER       NOT NULL,
    created_at                  TIMESTAMPTZ   NOT NULL,
    last_modified_at            TIMESTAMPTZ   NOT NULL,
    terminal_at                 TIMESTAMPTZ   NULL,
    error_code                  VARCHAR(128)  NULL,
    error_message               VARCHAR(2048) NULL,

    CONSTRAINT ak_workflow_run_tool_call_scope UNIQUE (id, team_id, workflow_run_id),
    CONSTRAINT fk_workflow_run_tool_call_run FOREIGN KEY (team_id, workflow_run_id)
        REFERENCES workflow_run (team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_tool_call_capture_completeness CHECK (
        capture_completeness IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown')),
    CONSTRAINT ck_workflow_run_tool_call_effect_class CHECK (
        effect_class IN ('ReadOnly', 'SideEffecting', 'Unknown')),
    CONSTRAINT ck_workflow_run_tool_call_error CHECK (
        (error_code IS NULL AND error_message IS NULL)
        OR (error_code IS NOT NULL AND btrim(error_code) <> '')),
    CONSTRAINT ck_workflow_run_tool_call_execution_identity CHECK (
        (execution_attempt_id IS NULL AND execution_attempt_ordinal IS NULL AND execution_generation IS NULL)
        OR (execution_attempt_id IS NOT NULL AND execution_attempt_ordinal IS NOT NULL AND execution_attempt_ordinal > 0
            AND (execution_generation IS NULL OR execution_generation > 0))),
    CONSTRAINT ck_workflow_run_tool_call_head CHECK (
        call_ordinal > 0 AND attempt_count >= 0 AND next_attempt_ordinal = attempt_count + 1
        AND revision > 0 AND schema_version > 0),
    -- A versioned tool_kind for the same reason harness_type_key is versioned: a row read a year from now must be
    -- interpretable against the tool contract that produced it, not against whatever that name has since come to mean.
    CONSTRAINT ck_workflow_run_tool_call_identity CHECK (
        tool_kind ~ '^[a-z0-9][a-z0-9._-]{0,126}/v[1-9][0-9]*$'
        AND btrim(tool_name) <> '' AND btrim(purpose) <> ''
        AND (tool_namespace IS NULL OR btrim(tool_namespace) <> '')),
    -- The three exhaustive, mutually exclusive states of the arguments axis: nothing stated, deliberately withheld,
    -- or captured under a named redaction pass. There is no fourth combination, so "I stored raw arguments" and
    -- "I claimed exact capture of content I never referenced" are both unwritable rather than merely discouraged.
    --
    -- Every comparison on a NULLABLE column is paired with its own IS NOT NULL in the same conjunction, and that is
    -- load-bearing rather than defensive: a PostgreSQL CHECK admits a row when it evaluates to TRUE *or NULL*. Written
    -- as a bare `arguments_digest ~ '...'`, a reference with no digest evaluates the arm to NULL, every other arm to
    -- FALSE, and the constraint to NULL — which ADMITS exactly the unverifiable reference it exists to refuse.
    -- `X IS NOT NULL AND <compare X>` is FALSE, never NULL, whatever order the planner evaluates it in.
    CONSTRAINT ck_workflow_run_tool_call_redaction CHECK (
        (arguments_redaction IS NULL AND arguments_artifact_id IS NULL AND arguments_digest IS NULL
            AND redaction_policy IS NULL AND capture_completeness NOT IN ('Exact', 'RedactedExact'))
        OR (arguments_redaction IS NOT NULL AND arguments_redaction = 'Withheld'
            AND arguments_artifact_id IS NULL AND arguments_digest IS NULL
            AND redaction_policy IS NULL AND capture_completeness = 'Unavailable')
        OR (arguments_redaction IS NOT NULL AND arguments_redaction IN ('None', 'Masked')
            AND arguments_artifact_id IS NOT NULL
            AND arguments_digest IS NOT NULL AND arguments_digest ~ '^[0-9a-f]{64}$'
            AND redaction_policy IS NOT NULL AND btrim(redaction_policy) <> ''
            AND (arguments_redaction <> 'Masked' OR capture_completeness <> 'Exact'))),
    CONSTRAINT ck_workflow_run_tool_call_source_identity CHECK (
        (source_kind IS NULL AND source_correlation_id IS NULL)
        OR (source_kind IS NOT NULL AND btrim(source_kind) <> ''
            AND source_correlation_id IS NOT NULL
            AND source_correlation_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
    CONSTRAINT ck_workflow_run_tool_call_state CHECK (
        state IN ('Pending', 'Running', 'Completed', 'Abandoned')),
    CONSTRAINT ck_workflow_run_tool_call_terminal CHECK (
        (state IN ('Pending', 'Running') AND terminal_at IS NULL AND error_code IS NULL)
        OR (state = 'Completed' AND terminal_at IS NOT NULL AND attempt_count > 0)
        OR (state = 'Abandoned' AND terminal_at IS NOT NULL AND error_code IS NOT NULL)),
    CONSTRAINT ck_workflow_run_tool_call_time CHECK (
        last_modified_at >= created_at
        AND (terminal_at IS NULL OR (terminal_at >= created_at AND last_modified_at >= terminal_at))),
    CONSTRAINT ck_workflow_run_tool_call_work_unit_identity CHECK (
        (work_plan_id IS NULL AND plan_version IS NULL AND work_unit_id IS NULL AND work_unit_contract_hash IS NULL)
        OR (work_plan_id IS NOT NULL AND plan_version IS NOT NULL AND plan_version > 0
            AND work_unit_id IS NOT NULL AND btrim(work_unit_id) <> ''))
);

CREATE INDEX ix_workflow_run_tool_call_run_created ON workflow_run_tool_call (workflow_run_id, created_at, id);
CREATE INDEX ix_workflow_run_tool_call_team_created ON workflow_run_tool_call (team_id, created_at, id);
CREATE INDEX ix_workflow_run_tool_call_execution_attempt ON workflow_run_tool_call (execution_attempt_id, call_ordinal) WHERE execution_attempt_id IS NOT NULL;
CREATE INDEX ix_workflow_run_tool_call_work_unit ON workflow_run_tool_call (work_plan_id, plan_version, work_unit_id) WHERE work_plan_id IS NOT NULL;
CREATE INDEX ix_workflow_run_tool_call_model_call ON workflow_run_tool_call (model_call_id, call_ordinal) WHERE model_call_id IS NOT NULL;
-- "Which tool did what" is a by-tool scan, and it is useless without the tenant prefix.
CREATE INDEX ix_workflow_run_tool_call_tool ON workflow_run_tool_call (team_id, tool_kind, tool_name, created_at);
-- The audit's hottest question — every side effect in a window — kept partial so it does not grow with read-only traffic.
CREATE INDEX ix_workflow_run_tool_call_side_effecting ON workflow_run_tool_call (team_id, created_at, id) WHERE effect_class = 'SideEffecting';
-- An invocation whose last attempt never reported is invisible to a created_at scan once the run is old; leading on
-- last_modified_at (no team prefix) so one sweep covers every tenant, as ix_..._harness_execution_stale_live does.
CREATE INDEX ix_workflow_run_tool_call_stale_live ON workflow_run_tool_call (last_modified_at, team_id, id) WHERE state IN ('Pending', 'Running');
CREATE UNIQUE INDEX ux_workflow_run_tool_call_source_identity
    ON workflow_run_tool_call (team_id, workflow_run_id, source_kind, source_correlation_id)
    WHERE source_correlation_id IS NOT NULL;

CREATE TABLE workflow_run_tool_call_attempt (
    id                       UUID          NOT NULL PRIMARY KEY,
    team_id                  UUID          NOT NULL,
    workflow_run_id          UUID          NOT NULL,
    tool_call_id             UUID          NOT NULL,
    attempt_ordinal          INTEGER       NOT NULL,
    retry_of_attempt_id      UUID          NULL,
    retry_reason             VARCHAR(128)  NULL,
    transport_kind           VARCHAR(64)   NULL,
    endpoint_fingerprint     VARCHAR(256)  NULL,
    invocation_id            VARCHAR(512)  NULL,
    status                   VARCHAR(32)   NOT NULL,
    result_artifact_id       UUID          NULL,
    result_digest            VARCHAR(64)   NULL,
    error_artifact_id        UUID          NULL,
    error_digest             VARCHAR(64)   NULL,
    result_redaction         VARCHAR(16)   NULL,
    redaction_policy         VARCHAR(200)  NULL,
    capture_source           VARCHAR(64)   NOT NULL,
    capture_completeness     VARCHAR(20)   NOT NULL,
    started_at               TIMESTAMPTZ   NOT NULL,
    completed_at             TIMESTAMPTZ   NULL,
    revision                 BIGINT        NOT NULL,
    schema_version           INTEGER       NOT NULL,
    created_at               TIMESTAMPTZ   NOT NULL,
    last_modified_at         TIMESTAMPTZ   NOT NULL,
    error_code               VARCHAR(200)  NULL,
    error_message            VARCHAR(2048) NULL,

    -- The composite parent key proves this attempt's denormalized team/run scope belongs to its exact logical call,
    -- rather than trusting a producer to stamp the same two values twice.
    CONSTRAINT fk_workflow_run_tool_call_attempt_call FOREIGN KEY (tool_call_id, team_id, workflow_run_id)
        REFERENCES workflow_run_tool_call (id, team_id, workflow_run_id) ON DELETE CASCADE,
    -- ...and this one lets retry_of_attempt_id prove the retried attempt belongs to the SAME call. Lower-ordinal and
    -- terminality are the guard's, because a foreign key can prove membership but not order.
    CONSTRAINT ak_workflow_run_tool_call_attempt_scope UNIQUE (id, team_id, tool_call_id),
    CONSTRAINT fk_workflow_run_tool_call_attempt_retry FOREIGN KEY (retry_of_attempt_id, team_id, tool_call_id)
        REFERENCES workflow_run_tool_call_attempt (id, team_id, tool_call_id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_tool_call_attempt_bounds CHECK (
        attempt_ordinal > 0 AND revision > 0 AND schema_version > 0),
    CONSTRAINT ck_workflow_run_tool_call_attempt_capture_completeness CHECK (
        capture_completeness IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown')),
    CONSTRAINT ck_workflow_run_tool_call_attempt_error CHECK (
        (error_code IS NULL AND error_message IS NULL)
        OR (error_code IS NOT NULL AND btrim(error_code) <> '')),
    CONSTRAINT ck_workflow_run_tool_call_attempt_identity CHECK (
        (transport_kind IS NULL OR btrim(transport_kind) <> '')
        AND (endpoint_fingerprint IS NULL OR btrim(endpoint_fingerprint) <> '')
        AND (invocation_id IS NULL OR btrim(invocation_id) <> '')),
    -- The first try retries nothing, and a retry that cannot say why it happened is the fact the audit needed most.
    CONSTRAINT ck_workflow_run_tool_call_attempt_retry CHECK (
        (retry_of_attempt_id IS NULL AND retry_reason IS NULL)
        OR (retry_of_attempt_id IS NOT NULL AND attempt_ordinal > 1
            AND retry_reason IS NOT NULL AND btrim(retry_reason) <> '')),
    -- The same three-arm, NULL-safe state as the call's arguments axis, governing BOTH returned payloads: a tool's
    -- error body is as capable of quoting a credential back as its success body, so they share one redaction statement
    -- rather than letting the error path be the one nobody declared — and each referenced payload carries its OWN
    -- digest, or the error path becomes the one nobody can verify instead. capture_completeness describes the RESULT,
    -- so an attempt carrying only a transport error body cannot claim exactness — there is no result to be exact about.
    CONSTRAINT ck_workflow_run_tool_call_attempt_redaction CHECK (
        (result_redaction IS NULL AND result_artifact_id IS NULL AND error_artifact_id IS NULL
            AND result_digest IS NULL AND error_digest IS NULL AND redaction_policy IS NULL
            AND capture_completeness NOT IN ('Exact', 'RedactedExact'))
        OR (result_redaction IS NOT NULL AND result_redaction = 'Withheld'
            AND result_artifact_id IS NULL AND error_artifact_id IS NULL
            AND result_digest IS NULL AND error_digest IS NULL
            AND redaction_policy IS NULL AND capture_completeness = 'Unavailable')
        OR (result_redaction IS NOT NULL AND result_redaction IN ('None', 'Masked')
            AND (result_artifact_id IS NOT NULL OR error_artifact_id IS NOT NULL)
            AND redaction_policy IS NOT NULL AND btrim(redaction_policy) <> ''
            AND (result_artifact_id IS NULL OR (result_digest IS NOT NULL AND result_digest ~ '^[0-9a-f]{64}$'))
            AND (result_artifact_id IS NOT NULL OR result_digest IS NULL)
            AND (error_artifact_id IS NULL OR (error_digest IS NOT NULL AND error_digest ~ '^[0-9a-f]{64}$'))
            AND (error_artifact_id IS NOT NULL OR error_digest IS NULL)
            AND (result_artifact_id IS NOT NULL OR capture_completeness NOT IN ('Exact', 'RedactedExact'))
            AND (result_redaction <> 'Masked' OR capture_completeness <> 'Exact'))),
    CONSTRAINT ck_workflow_run_tool_call_attempt_status CHECK (
        status IN ('Pending', 'Running', 'Succeeded', 'Failed', 'Denied', 'Cancelled', 'TimedOut', 'Indeterminate')),
    -- A non-Succeeded terminal owes a typed reason, so an unknown outcome can never be read as a clean one.
    CONSTRAINT ck_workflow_run_tool_call_attempt_terminal CHECK (
        (status IN ('Pending', 'Running') AND completed_at IS NULL AND error_code IS NULL)
        OR (status = 'Succeeded' AND completed_at IS NOT NULL AND error_code IS NULL)
        OR (status IN ('Failed', 'Denied', 'Cancelled', 'TimedOut', 'Indeterminate')
            AND completed_at IS NOT NULL AND error_code IS NOT NULL)),
    CONSTRAINT ck_workflow_run_tool_call_attempt_time CHECK (
        created_at >= started_at AND last_modified_at >= created_at
        AND (completed_at IS NULL OR (completed_at >= started_at AND last_modified_at >= completed_at)))
);

CREATE UNIQUE INDEX ux_workflow_run_tool_call_attempt_ordinal
    ON workflow_run_tool_call_attempt (team_id, tool_call_id, attempt_ordinal);
-- The one-in-flight invariant's CONCURRENCY backstop. In one session the guard refuses a second live attempt first,
-- but two writers racing past their own snapshots see no conflict and only this index does.
CREATE UNIQUE INDEX ux_workflow_run_tool_call_attempt_in_flight
    ON workflow_run_tool_call_attempt (team_id, tool_call_id)
    WHERE status IN ('Pending', 'Running');
-- One physical try per fabric request id, which is the idempotent admission key a capture adapter replays against.
CREATE UNIQUE INDEX ux_workflow_run_tool_call_attempt_invocation
    ON workflow_run_tool_call_attempt (team_id, tool_call_id, invocation_id)
    WHERE invocation_id IS NOT NULL;
CREATE INDEX ix_workflow_run_tool_call_attempt_run_started ON workflow_run_tool_call_attempt (workflow_run_id, started_at, id);
CREATE INDEX ix_workflow_run_tool_call_attempt_team_started ON workflow_run_tool_call_attempt (team_id, started_at, id);
CREATE INDEX ix_workflow_run_tool_call_attempt_retry ON workflow_run_tool_call_attempt (retry_of_attempt_id) WHERE retry_of_attempt_id IS NOT NULL;

CREATE OR REPLACE FUNCTION workflow_run_tool_call_guard() RETURNS trigger AS $$
DECLARE
    appended workflow_run_tool_call_attempt%ROWTYPE;
    live_attempt_id UUID;
    unknown_attempt_id UUID;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.state <> 'Pending' OR NEW.revision <> 1 OR NEW.attempt_count <> 0 OR NEW.next_attempt_ordinal <> 1
           OR NEW.terminal_at IS NOT NULL OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL
           OR NEW.last_modified_at IS DISTINCT FROM NEW.created_at THEN
            RAISE EXCEPTION 'workflow_run_tool_call must start as an empty Pending revision-one invocation (id=%).', NEW.id;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id OR NEW.node_id IS DISTINCT FROM OLD.node_id
       OR NEW.iteration_key IS DISTINCT FROM OLD.iteration_key OR NEW.work_plan_id IS DISTINCT FROM OLD.work_plan_id
       OR NEW.plan_version IS DISTINCT FROM OLD.plan_version OR NEW.work_unit_id IS DISTINCT FROM OLD.work_unit_id
       OR NEW.work_unit_contract_hash IS DISTINCT FROM OLD.work_unit_contract_hash
       OR NEW.execution_attempt_id IS DISTINCT FROM OLD.execution_attempt_id
       OR NEW.execution_attempt_ordinal IS DISTINCT FROM OLD.execution_attempt_ordinal
       OR NEW.execution_generation IS DISTINCT FROM OLD.execution_generation
       OR NEW.call_ordinal IS DISTINCT FROM OLD.call_ordinal OR NEW.model_call_id IS DISTINCT FROM OLD.model_call_id
       OR NEW.purpose IS DISTINCT FROM OLD.purpose OR NEW.tool_kind IS DISTINCT FROM OLD.tool_kind
       OR NEW.tool_namespace IS DISTINCT FROM OLD.tool_namespace OR NEW.tool_name IS DISTINCT FROM OLD.tool_name
       OR NEW.effect_class IS DISTINCT FROM OLD.effect_class OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'workflow_run_tool_call stable invocation identity is immutable (id=%).', OLD.id;
    END IF;

    -- 0130's source-admission rule: which source a projected row came from can never be restated, or the same source
    -- fact is admissible twice under two identities and ux_..._source_identity stops dedupliating anything.
    IF NEW.source_kind IS DISTINCT FROM OLD.source_kind
       OR NEW.source_correlation_id IS DISTINCT FROM OLD.source_correlation_id THEN
        RAISE EXCEPTION 'workflow_run_tool_call source identity is immutable (id=%, old_source=%/%, new_source=%/%).', OLD.id, OLD.source_kind, OLD.source_correlation_id, NEW.source_kind, NEW.source_correlation_id;
    END IF;

    -- Late argument evidence may FILL an unstated axis exactly once. Replacing a stated one would silently rewrite
    -- what a reader already audited — and upgrading a Withheld decision into bytes would retroactively contradict a
    -- deliberate choice not to capture them. capture_completeness is deliberately NOT one of the pinned columns, so an
    -- evidence downgrade found while the call is still live can still be recorded; once the row is terminal the check
    -- below freezes every column including this one, so a corruption discovered after close is a new observation for a
    -- later slice to model, not an edit to this row.
    IF OLD.arguments_redaction IS NOT NULL
       AND (NEW.arguments_redaction IS DISTINCT FROM OLD.arguments_redaction
            OR NEW.arguments_artifact_id IS DISTINCT FROM OLD.arguments_artifact_id
            OR NEW.arguments_digest IS DISTINCT FROM OLD.arguments_digest
            OR NEW.redaction_policy IS DISTINCT FROM OLD.redaction_policy) THEN
        RAISE EXCEPTION 'workflow_run_tool_call stated arguments capture is immutable (id=%, redaction=%).', OLD.id, OLD.arguments_redaction;
    END IF;

    IF OLD.state IN ('Completed', 'Abandoned') THEN
        RAISE EXCEPTION 'workflow_run_tool_call terminal state is immutable (id=%, state=%).', OLD.id, OLD.state;
    END IF;

    IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
        RAISE EXCEPTION 'workflow_run_tool_call revision must advance exactly once and time must not rewind (id=%, old_revision=%, new_revision=%).', OLD.id, OLD.revision, NEW.revision;
    END IF;

    IF NEW.attempt_count IS DISTINCT FROM OLD.attempt_count THEN
        IF NEW.attempt_count <> OLD.attempt_count + 1 OR NEW.next_attempt_ordinal <> OLD.next_attempt_ordinal + 1
           OR NEW.state <> 'Running' OR NEW.terminal_at IS NOT NULL THEN
            RAISE EXCEPTION 'workflow_run_tool_call attempt-head advances are exactly one appended attempt (id=%).', OLD.id;
        END IF;

        SELECT * INTO appended FROM workflow_run_tool_call_attempt
        WHERE team_id = NEW.team_id AND tool_call_id = NEW.id AND attempt_ordinal = OLD.next_attempt_ordinal;
        IF NOT FOUND OR appended.created_at > NEW.last_modified_at THEN
            RAISE EXCEPTION 'workflow_run_tool_call head advance requires its exact appended attempt (id=%, ordinal=%).', OLD.id, OLD.next_attempt_ordinal;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.state IS DISTINCT FROM OLD.state THEN
        IF NEW.state NOT IN ('Completed', 'Abandoned') OR NEW.terminal_at IS NULL OR NEW.terminal_at < OLD.created_at THEN
            RAISE EXCEPTION 'workflow_run_tool_call illegal state transition (id=%, old=%, new=%).', OLD.id, OLD.state, NEW.state;
        END IF;

        -- Half of "a terminal call's attempts are all terminal". The other half is the attempt guard refusing to
        -- append to, or revive an attempt of, a call that is already closed.
        SELECT id INTO live_attempt_id FROM workflow_run_tool_call_attempt
        WHERE team_id = OLD.team_id AND tool_call_id = OLD.id AND status IN ('Pending', 'Running')
        ORDER BY attempt_ordinal
        LIMIT 1
        FOR SHARE;
        IF FOUND THEN
            RAISE EXCEPTION 'workflow_run_tool_call cannot close while an attempt is still in flight (id=%, attempt_id=%).', OLD.id, live_attempt_id;
        END IF;

        -- ...and a call whose real outcome is unknown may not close as a CLEAN one. Indeterminate is the single status
        -- meaning "this side effect may or may not have landed", so rolling it up into a Completed with no error_code
        -- is the same collapse the attempt status forbids one level down. Such a call closes Abandoned, which owes an
        -- error_code. Failed/Denied/Cancelled/TimedOut are KNOWN outcomes, so a retried-then-succeeded call still
        -- closes Completed — the retry lineage this plane exists to keep is unaffected.
        IF NEW.state = 'Completed' THEN
            SELECT id INTO unknown_attempt_id FROM workflow_run_tool_call_attempt
            WHERE team_id = OLD.team_id AND tool_call_id = OLD.id AND status = 'Indeterminate'
            ORDER BY attempt_ordinal
            LIMIT 1
            FOR SHARE;
            IF FOUND THEN
                RAISE EXCEPTION 'workflow_run_tool_call cannot close as Completed over an attempt whose effect is unknown (id=%, attempt_id=%).', OLD.id, unknown_attempt_id;
            END IF;
        END IF;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_tool_call_enforce_invariants
    BEFORE INSERT OR UPDATE ON workflow_run_tool_call
    FOR EACH ROW EXECUTE FUNCTION workflow_run_tool_call_guard();

CREATE OR REPLACE FUNCTION workflow_run_tool_call_attempt_guard() RETURNS trigger AS $$
DECLARE
    call_row workflow_run_tool_call%ROWTYPE;
    live_attempt_id UUID;
    retried workflow_run_tool_call_attempt%ROWTYPE;
BEGIN
    -- attempt_count and next_attempt_ordinal are a DERIVED head that no DELETE walks back, so a piecemeal attempt
    -- delete leaves the call naming a try that no longer exists — and closing as Completed still passes
    -- ck_..._terminal's attempt_count > 0 arm on the strength of it. Pruning stays CALL-level: PostgreSQL removes the
    -- parent tuple before ON DELETE CASCADE reaches the children, so the cascade finds no call row here and is
    -- admitted, while a bare DELETE against this table finds one and is refused.
    IF TG_OP = 'DELETE' THEN
        PERFORM 1 FROM workflow_run_tool_call WHERE team_id = OLD.team_id AND id = OLD.tool_call_id;
        IF FOUND THEN
            RAISE EXCEPTION 'workflow_run_tool_call_attempt cannot be deleted while its call still exists — prune the call and let the cascade take its tries (id=%, tool_call_id=%).', OLD.id, OLD.tool_call_id;
        END IF;
        RETURN OLD;
    END IF;

    IF TG_OP = 'INSERT' THEN
        SELECT * INTO call_row FROM workflow_run_tool_call
        WHERE team_id = NEW.team_id AND id = NEW.tool_call_id AND workflow_run_id = NEW.workflow_run_id
        FOR UPDATE;
        IF NOT FOUND OR call_row.state NOT IN ('Pending', 'Running') THEN
            RAISE EXCEPTION 'workflow_run_tool_call_attempt requires its live tenant-bound tool call (tool_call_id=%).', NEW.tool_call_id;
        END IF;
        IF NEW.attempt_ordinal <> call_row.next_attempt_ordinal THEN
            RAISE EXCEPTION 'workflow_run_tool_call_attempt ordinals are contiguous from one (tool_call_id=%, expected=%, attempted=%).', NEW.tool_call_id, call_row.next_attempt_ordinal, NEW.attempt_ordinal;
        END IF;

        -- Serialized behind the FOR UPDATE above; ux_..._in_flight is what catches two writers that never met here.
        SELECT id INTO live_attempt_id FROM workflow_run_tool_call_attempt
        WHERE team_id = NEW.team_id AND tool_call_id = NEW.tool_call_id AND status IN ('Pending', 'Running')
        LIMIT 1
        FOR SHARE;
        IF FOUND THEN
            RAISE EXCEPTION 'workflow_run_tool_call_attempt allows exactly one attempt in flight per tool call (tool_call_id=%, in_flight=%).', NEW.tool_call_id, live_attempt_id;
        END IF;

        IF NEW.status NOT IN ('Pending', 'Running') OR NEW.revision <> 1 OR NEW.completed_at IS NOT NULL
           OR NEW.error_code IS NOT NULL OR NEW.error_message IS NOT NULL
           OR NEW.last_modified_at IS DISTINCT FROM NEW.created_at THEN
            RAISE EXCEPTION 'workflow_run_tool_call_attempt must start as a live revision-one try (id=%).', NEW.id;
        END IF;

        -- Of the three conditions below only NOT FOUND is reachable TODAY — it is what catches a retry_of naming
        -- another call's attempt, and it fires here rather than at fk_..._retry because a BEFORE ROW trigger runs
        -- before constraint checks. The other two are implied by invariants enforced a few lines up: every existing
        -- attempt of this call has a lower ordinal (contiguity) and none is live (the one-in-flight gate). They are
        -- stated anyway so that relaxing either invariant later cannot silently legalise a forged lineage — but this
        -- comment must not be read as a claim that each has its own counter-example, because two of them cannot.
        IF NEW.retry_of_attempt_id IS NOT NULL THEN
            SELECT * INTO retried FROM workflow_run_tool_call_attempt
            WHERE team_id = NEW.team_id AND tool_call_id = NEW.tool_call_id AND id = NEW.retry_of_attempt_id
            FOR SHARE;
            IF NOT FOUND OR retried.attempt_ordinal >= NEW.attempt_ordinal
                OR retried.status IN ('Pending', 'Running') THEN
                RAISE EXCEPTION 'workflow_run_tool_call_attempt may only retry an earlier finished attempt of the same call (id=%, retry_of=%).', NEW.id, NEW.retry_of_attempt_id;
            END IF;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id
       OR NEW.tool_call_id IS DISTINCT FROM OLD.tool_call_id
       OR NEW.attempt_ordinal IS DISTINCT FROM OLD.attempt_ordinal
       OR NEW.retry_of_attempt_id IS DISTINCT FROM OLD.retry_of_attempt_id
       OR NEW.retry_reason IS DISTINCT FROM OLD.retry_reason
       OR NEW.transport_kind IS DISTINCT FROM OLD.transport_kind
       OR NEW.endpoint_fingerprint IS DISTINCT FROM OLD.endpoint_fingerprint
       OR NEW.invocation_id IS DISTINCT FROM OLD.invocation_id
       OR NEW.started_at IS DISTINCT FROM OLD.started_at OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'workflow_run_tool_call_attempt stable try identity is immutable (id=%).', OLD.id;
    END IF;

    IF OLD.status NOT IN ('Pending', 'Running') THEN
        RAISE EXCEPTION 'workflow_run_tool_call_attempt terminal status is immutable (id=%, status=%).', OLD.id, OLD.status;
    END IF;

    IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
        RAISE EXCEPTION 'workflow_run_tool_call_attempt revision must advance exactly once and time must not rewind (id=%, old_revision=%, new_revision=%).', OLD.id, OLD.revision, NEW.revision;
    END IF;

    IF OLD.result_redaction IS NOT NULL
       AND (NEW.result_redaction IS DISTINCT FROM OLD.result_redaction
            OR NEW.result_artifact_id IS DISTINCT FROM OLD.result_artifact_id
            OR NEW.result_digest IS DISTINCT FROM OLD.result_digest
            OR NEW.error_artifact_id IS DISTINCT FROM OLD.error_artifact_id
            OR NEW.error_digest IS DISTINCT FROM OLD.error_digest
            OR NEW.redaction_policy IS DISTINCT FROM OLD.redaction_policy) THEN
        RAISE EXCEPTION 'workflow_run_tool_call_attempt stated result capture is immutable (id=%, redaction=%).', OLD.id, OLD.result_redaction;
    END IF;

    -- Pending -> Running, Pending -> terminal (a synchronous call) and Running -> terminal are the only hops. A
    -- terminal status is already refused above, so re-pending a live try is all that is left to reject: it would
    -- reopen an attempt the one-in-flight index has already counted.
    IF NEW.status IS DISTINCT FROM OLD.status AND NEW.status = 'Pending' THEN
        RAISE EXCEPTION 'workflow_run_tool_call_attempt cannot return to Pending (id=%, old=%).', OLD.id, OLD.status;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_tool_call_attempt_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_tool_call_attempt
    FOR EACH ROW EXECUTE FUNCTION workflow_run_tool_call_attempt_guard();

-- The head is advanced by the DATABASE, never by a writer: that is what makes next_attempt_ordinal the only ordinal
-- the following attempt may carry, and therefore what makes the ordinals contiguous rather than conventionally so.
CREATE OR REPLACE FUNCTION workflow_run_tool_call_attempt_advance_head() RETURNS trigger AS $$
BEGIN
    UPDATE workflow_run_tool_call SET
        revision = revision + 1,
        state = 'Running',
        attempt_count = attempt_count + 1,
        next_attempt_ordinal = next_attempt_ordinal + 1,
        last_modified_at = GREATEST(last_modified_at, NEW.created_at)
    WHERE team_id = NEW.team_id AND id = NEW.tool_call_id AND workflow_run_id = NEW.workflow_run_id;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_tool_call_attempt_advance_call_head
    AFTER INSERT ON workflow_run_tool_call_attempt
    FOR EACH ROW EXECUTE FUNCTION workflow_run_tool_call_attempt_advance_head();

COMMENT ON TABLE workflow_run_tool_call IS
    'One LOGICAL tool invocation in a workflow run: which tool, read-only or side-effecting, and the canonical redacted arguments. Observation only — tool_call_ledger keeps governance and exactly-once.';
COMMENT ON TABLE workflow_run_tool_call_attempt IS
    'One PHYSICAL try of a logical tool call. Ordinals are contiguous from one, exactly one try is in flight at a time, and a closed call has no live try.';
COMMENT ON COLUMN workflow_run_tool_call.effect_class IS
    'Whether the tool could mutate anything: ReadOnly, SideEffecting, or Unknown. Three-valued because an unobserved effect class defaulted either way is a lie — ReadOnly understates the risk and SideEffecting overstates the evidence.';
COMMENT ON COLUMN workflow_run_tool_call.arguments_redaction IS
    'How the referenced argument bytes relate to the wire (None/Masked/Withheld), in NativeRecordV1 vocabulary. NULLABLE on purpose: NULL is the birth state meaning no bytes were captured, so there is no redaction to claim. It is fillable exactly once, and immutable once stated. What forbids an undeclared credential-bearing payload is ck_workflow_run_tool_call_redaction, which admits a referenced artifact only under a stated redaction — not a NOT NULL on this column, which the three-arm birth state depends on not having.';
COMMENT ON COLUMN workflow_run_tool_call.redaction_policy IS
    'The named pass that produced the referenced bytes; required whenever an artifact is referenced. It proves a redactor RAN, not that it was correct — the database cannot inspect artifact bytes.';
COMMENT ON COLUMN workflow_run_tool_call.source_correlation_id IS
    'Stable logical source identity when this row is a projection. For tool-call-ledger/v1 it is that ledger row id; it deduplicates the PROJECTION, never the invocation, whose exactly-once authority stays ux_tool_call_ledger_run_key.';
COMMENT ON COLUMN workflow_run_tool_call.state IS
    'Invocation lifecycle only, never the task verdict. Completed requires at least one attempt and refuses to close over an Indeterminate one; such a call closes Abandoned, which requires an error_code — so a call whose real outcome is unknown can never read as a clean one.';
COMMENT ON COLUMN workflow_run_tool_call.next_attempt_ordinal IS
    'The only ordinal the next appended attempt may carry. Advanced by the AFTER-INSERT trigger, never by a writer, which is what makes the ordinals contiguous from one.';
COMMENT ON COLUMN workflow_run_tool_call_attempt.retry_of_attempt_id IS
    'The earlier finished attempt of the SAME call this try retries. Not derivable as ordinal minus one: a third try may re-issue the first request rather than the second.';
COMMENT ON COLUMN workflow_run_tool_call_attempt.status IS
    'Observed outcome of this physical try. Denied is an observed outcome (nothing executed), not a governance state — AwaitingApproval and Expired stay in tool_call_ledger, which owns the approval state machine.';
COMMENT ON COLUMN workflow_run_tool_call_attempt.endpoint_fingerprint IS
    'Sanitized endpoint identity for the tool fabric; never a URL carrying credentials or secret query parameters.';
