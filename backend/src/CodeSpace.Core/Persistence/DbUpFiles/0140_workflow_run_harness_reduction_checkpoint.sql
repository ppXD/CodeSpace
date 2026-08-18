-- 0140_workflow_run_harness_reduction_checkpoint.sql
--
-- The durable POSITION AND REDUCED STATE of an incremental reduction over one harness execution's captured records.
-- Today a re-attach folds only the post-attach tail: the executor's fold is O(1) per event (#1479) but it starts from
-- NOTHING on a re-attach, because there is no durable record of how far the fold got or what it had reduced. A fact
-- the harness stated exactly once before the re-attach — the session id a warm resume needs, the first model call
-- that names the model, the prefix of the transcript — is simply gone, and the recovered state is silently a
-- different value from the one a whole-stream fold would have produced.
--
-- One row per (execution, reducer kind). REDUCER KIND carries its own /vN, and it is IMMUTABLE, so a reduction whose
-- state shape changes is a NEW kind — a new row beside the old one — rather than a rewrite that hands an old reader a
-- state it cannot parse. contract_version is immutable for the same reason.
--
-- INVARIANTS, all enforced in the database rather than by a convention a writer may forget:
--   * THREE independent statements of the same number must agree in every statement that writes the row: the
--     frontier's own sum, the records_consumed column, and the reduced state's own recordsConsumed field. This is
--     what makes "a checkpoint may never claim a position the reducer has not actually consumed" refusable rather
--     than merely intended — bumping the frontier without the state, or the state without the frontier, is rejected.
--   * the frontier is MONOTONIC per stream and no stream may leave it. This is the ONE anti-resurrection invariant the
--     database itself holds: a resurrected reducer cannot rewind a newer checkpoint to its own older prefix even if it
--     wins the write race. It bounds the damage; it does not authenticate the writer (see below).
--   * ACQUIRING the reducer lease advances reducer_fence by exactly one, and an acquisition over a lease that is
--     still live is refused — so each fence value is acquired by at most ONE owner. The claim arm enters on EITHER
--     axis (fence changed OR owner changed to somebody), following 0132's and 0137's arms, because an owner swap that
--     leaves the fence untouched is exactly how a live lease would otherwise be taken.
--   * a claim may not move the position or the state — so a lease grab can never be a way to smuggle a reduction in.
--   * the row is durable: DELETE is refused, revision advances by exactly one, time never rewinds, and stable identity
--     (execution, reducer kind, contract version, birth time) is immutable. There is no terminal state — a reduction
--     that has caught up is simply one whose frontier stops moving, so nothing has to decide when it is "finished".
--
-- WHAT THIS SCHEMA DOES NOT PROVE, and therefore what every writer still owes. A row trigger sees only OLD and NEW,
-- never WHICH worker issued the statement, so holdership is not authenticated here:
--   * NOTHING here refuses a DISPLACED reducer's advance. A reducer that lost the lease, kept folding from its own
--     in-memory state and writes a frontier that is not behind the stored one is accepted, and the row silently
--     becomes its reduction rather than the current holder's. A holder proves itself only by carrying its own
--     predicate: WHERE reducer_owner_id = <me> AND reducer_fence = <observed> AND revision = <observed>. Omit it and
--     a third party's advance wins silently. That predicate, not the fence, is what makes a displaced reducer's next
--     write fail instead of interleave — exactly as 0137 states it.
--     A column recording "the fence I am advancing under" would NOT close this, which is why there is none: NEW
--     carries the STORED value for every column an UPDATE omits, so a check comparing it to the row's own fence is
--     silently satisfied by the very writer it would exist to refuse, while reading like protection.
--   * RELEASING a live lease (owner -> NULL, fence unchanged) is legal from any session, exactly as in 0137.
--   * the frontier's per-stream ordinals are checked for MONOTONICITY, never for having actually been read: the
--     database cannot see the records. That the frontier corresponds to real records is the reducer's obligation,
--     and it discharges it by folding forward from this row and never writing a position it did not consume.
--   * nothing here reduces anything. The state is an opaque JSON object to this schema apart from the three fields the
--     guard cross-checks — recordsConsumed, contractVersion and prefixDigest — and every other field could hold any
--     value the writer likes. The reduction's own field-by-field equality with a whole-stream fold is proved in tests,
--     never here.
--
-- This row is BOOKKEEPING, never authority: agent_run.status remains the only outcome authority, and no column here
-- is read by completion, terminal decision, planner, oracle or model routing. Nothing reads or writes this table in
-- production yet.
-- Rollback: DROP TABLE workflow_run_harness_reduction_checkpoint; DROP FUNCTION workflow_run_harness_reduction_position_total;

CREATE TABLE workflow_run_harness_reduction_checkpoint (
    id                        UUID          NOT NULL PRIMARY KEY,
    team_id                   UUID          NOT NULL REFERENCES team(id) ON DELETE RESTRICT,
    agent_run_id              UUID          NOT NULL,
    execution_id              UUID          NOT NULL,
    reducer_kind              VARCHAR(80)   NOT NULL,
    contract_version          INTEGER       NOT NULL,
    position_jsonb            JSONB         NOT NULL,
    records_consumed          BIGINT        NOT NULL,
    reduced_state_jsonb       JSONB         NOT NULL,
    reducer_owner_id          UUID          NULL,
    reducer_fence             BIGINT        NOT NULL,
    reducer_lease_expires_at  TIMESTAMPTZ   NULL,
    revision                  BIGINT        NOT NULL,
    created_at                TIMESTAMPTZ   NOT NULL,
    last_modified_at          TIMESTAMPTZ   NOT NULL,

    CONSTRAINT fk_workflow_run_harness_reduction_checkpoint_execution FOREIGN KEY (team_id, execution_id, agent_run_id)
        REFERENCES workflow_run_harness_execution (team_id, id, agent_run_id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_harness_reduction_checkpoint_bounds CHECK (
        records_consumed >= 0 AND reducer_fence >= 0 AND revision > 0 AND contract_version > 0),
    CONSTRAINT ck_workflow_run_harness_reduction_checkpoint_claim CHECK (
        (reducer_owner_id IS NULL AND reducer_lease_expires_at IS NULL)
        OR (reducer_owner_id IS NOT NULL AND reducer_fence > 0 AND reducer_lease_expires_at IS NOT NULL)),
    CONSTRAINT ck_workflow_run_harness_reduction_checkpoint_kind CHECK (
        reducer_kind ~ '^[a-z0-9][a-z0-9._-]{0,62}/v[1-9][0-9]*$'),
    -- IS NOT DISTINCT FROM on the 'streams' arm, because a MISSING key makes jsonb_typeof() NULL, `TRUE AND NULL` is
    -- NULL, and a CHECK that evaluates to NULL is SATISFIED — so an `= 'array'` arm would have admitted '{}'.
    CONSTRAINT ck_workflow_run_harness_reduction_checkpoint_shape CHECK (
        jsonb_typeof(position_jsonb) = 'object' AND jsonb_typeof(position_jsonb -> 'streams') IS NOT DISTINCT FROM 'array'
        AND jsonb_typeof(reduced_state_jsonb) = 'object'),
    CONSTRAINT ck_workflow_run_harness_reduction_checkpoint_time CHECK (last_modified_at >= created_at)
);

CREATE UNIQUE INDEX ux_workflow_run_harness_reduction_checkpoint_reducer
    ON workflow_run_harness_reduction_checkpoint (team_id, execution_id, reducer_kind);
CREATE INDEX ix_workflow_run_harness_reduction_checkpoint_agent_run
    ON workflow_run_harness_reduction_checkpoint (team_id, agent_run_id, last_modified_at, id);
-- The scan a reaper needs to find a reduction whose owner went away: only a HELD lease can lapse, so the partial
-- index is on holdership rather than on expiry being non-null.
CREATE INDEX ix_workflow_run_harness_reduction_checkpoint_lease_expiry
    ON workflow_run_harness_reduction_checkpoint (reducer_lease_expires_at, team_id, id)
    WHERE reducer_owner_id IS NOT NULL;

-- The frontier's total, and NULL for any frontier that is not a well-formed one. Returning NULL rather than raising
-- keeps the caller's error message about the ROW instead of about a cast, and lets one call answer both "is this
-- readable?" and "how many records does it account for?".
--
-- Ordinals are ZERO-BASED within their stream (NativeRecordV1.Ordinal), so a stream whose frontier is next_ordinal=k
-- accounts for exactly k records — ordinals 0..k-1. The sum is therefore the exact record count, which is what makes
-- the count checkable against the frontier at all.
CREATE OR REPLACE FUNCTION workflow_run_harness_reduction_position_total(position_json JSONB) RETURNS BIGINT AS $$
DECLARE
    frontier_json JSONB;
    entry_count INTEGER;
    stream_count INTEGER;
    total BIGINT;
BEGIN
    -- IS DISTINCT FROM on the 'streams' arm for the same reason the shape CHECK uses it: a MISSING key makes
    -- jsonb_typeof() NULL, `FALSE OR FALSE OR NULL` is NULL, and `IF NULL THEN` is false — so a <> arm would have
    -- fallen THROUGH here, summed no rows and returned 0, which is a valid total for '{}'.
    IF position_json IS NULL OR jsonb_typeof(position_json) <> 'object'
       OR jsonb_typeof(position_json -> 'streams') IS DISTINCT FROM 'array' THEN
        RETURN NULL;
    END IF;

    frontier_json := position_json -> 'streams';

    -- Types first, and with IS DISTINCT FROM rather than <>: a MISSING key makes jsonb_typeof() NULL, an OR chain of
    -- NULLs is NULL, and a WHERE of NULL drops the row — so a <> chain would have accepted an entry with no ordinal at
    -- all and then summed it as zero.
    IF EXISTS (SELECT 1 FROM jsonb_array_elements(frontier_json) AS frontier(entry)
               WHERE jsonb_typeof(frontier.entry) IS DISTINCT FROM 'object'
                  OR jsonb_typeof(frontier.entry -> 'streamId') IS DISTINCT FROM 'string'
                  OR jsonb_typeof(frontier.entry -> 'nextOrdinal') IS DISTINCT FROM 'number') THEN
        RETURN NULL;
    END IF;

    -- Values second. Every cast and regex below reads a value whose type the pass above already proved.
    IF EXISTS (SELECT 1 FROM jsonb_array_elements(frontier_json) AS frontier(entry)
               WHERE frontier.entry ->> 'streamId' !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
                  OR (frontier.entry ->> 'nextOrdinal')::NUMERIC < 0
                  OR (frontier.entry ->> 'nextOrdinal')::NUMERIC <> trunc((frontier.entry ->> 'nextOrdinal')::NUMERIC)) THEN
        RETURN NULL;
    END IF;

    SELECT COUNT(*), COUNT(DISTINCT frontier.entry ->> 'streamId')
    INTO entry_count, stream_count
    FROM jsonb_array_elements(frontier_json) AS frontier(entry);
    IF entry_count <> stream_count THEN
        RETURN NULL;
    END IF;

    SELECT COALESCE(SUM((frontier.entry ->> 'nextOrdinal')::BIGINT), 0) INTO total
    FROM jsonb_array_elements(frontier_json) AS frontier(entry);

    RETURN total;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

CREATE OR REPLACE FUNCTION workflow_run_harness_reduction_checkpoint_guard() RETURNS trigger AS $$
DECLARE
    claimed_total BIGINT;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint is durable reduction state — DELETE rejected (id=%).', OLD.id;
    END IF;

    -- Every write, insert or update, must state the same consumed count three ways. Checked before anything else so
    -- a claim cannot slip an inconsistent state past on the grounds that it was "only taking the lease".
    claimed_total := workflow_run_harness_reduction_position_total(NEW.position_jsonb);
    IF claimed_total IS NULL THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint position must be distinct per-stream zero-based frontiers (id=%).', NEW.id;
    END IF;
    IF NEW.records_consumed <> claimed_total THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint cannot claim a position it has not consumed (id=%, frontier_total=%, claimed=%).', NEW.id, claimed_total, NEW.records_consumed;
    END IF;
    -- Each pair is TWO statements rather than one OR: a MISSING key makes jsonb_typeof() NULL, and `NULL OR NULL` is
    -- NULL, which an IF treats as false — so a single combined condition would wave through a state carrying no count
    -- at all. Proving the type first also means the cast below never sees a value it cannot parse.
    IF jsonb_typeof(NEW.reduced_state_jsonb -> 'recordsConsumed') IS DISTINCT FROM 'number' THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint reduced state must state the exact count it reduced (id=%, claimed=%, state=%).', NEW.id, NEW.records_consumed, NEW.reduced_state_jsonb -> 'recordsConsumed';
    END IF;
    IF (NEW.reduced_state_jsonb ->> 'recordsConsumed')::BIGINT <> NEW.records_consumed THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint reduced state must state the exact count it reduced (id=%, claimed=%, state=%).', NEW.id, NEW.records_consumed, NEW.reduced_state_jsonb ->> 'recordsConsumed';
    END IF;
    IF jsonb_typeof(NEW.reduced_state_jsonb -> 'contractVersion') IS DISTINCT FROM 'number' THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint reduced state must carry its own contract version (id=%, column=%).', NEW.id, NEW.contract_version;
    END IF;
    IF (NEW.reduced_state_jsonb ->> 'contractVersion')::INTEGER <> NEW.contract_version THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint reduced state must carry its own contract version (id=%, column=%).', NEW.id, NEW.contract_version;
    END IF;
    -- The prefix witness. Without it a state that reduced a different prefix is indistinguishable from this one, so a
    -- tail-only fold could be stored as if it were the whole prefix and nothing downstream could tell.
    IF COALESCE(NEW.reduced_state_jsonb ->> 'prefixDigest', '') !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint reduced state must carry a canonical prefix digest (id=%).', NEW.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        IF NEW.revision <> 1 OR NEW.reducer_fence <> 0
           OR NEW.reducer_owner_id IS NOT NULL OR NEW.reducer_lease_expires_at IS NOT NULL
           OR NEW.last_modified_at IS DISTINCT FROM NEW.created_at THEN
            RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint must start as an unclaimed revision-one row (id=%).', NEW.id;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.agent_run_id IS DISTINCT FROM OLD.agent_run_id OR NEW.execution_id IS DISTINCT FROM OLD.execution_id
       OR NEW.reducer_kind IS DISTINCT FROM OLD.reducer_kind
       OR NEW.contract_version IS DISTINCT FROM OLD.contract_version
       OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint stable reduction identity is immutable (id=%).', OLD.id;
    END IF;
    IF NEW.revision <> OLD.revision + 1 OR NEW.last_modified_at < OLD.last_modified_at THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint revision must advance exactly once and time must not rewind (id=%, old_revision=%, new_revision=%).', OLD.id, OLD.revision, NEW.revision;
    END IF;

    -- The frontier may gain a stream (a channel opened later) but may never lose one or rewind one. State plainly
    -- what that does and does not buy, because an earlier draft of this file claimed more: it is the ONE
    -- anti-resurrection invariant the DATABASE itself holds, and it holds exactly one thing — a displaced reducer
    -- cannot rewind the row to its own shorter prefix. It does NOT stop a displaced reducer whose frontier happens
    -- to be at or ahead of the stored one from writing; nothing here does, and no fence arm below does either.
    -- Holdership is proved by the writer's own WHERE clause over the owner and revision it observed, the way 0137
    -- proves it, not by this trigger.
    IF EXISTS (
        SELECT 1
        FROM jsonb_array_elements(OLD.position_jsonb -> 'streams') AS stored_frontier(entry)
        LEFT JOIN jsonb_array_elements(NEW.position_jsonb -> 'streams') AS written_frontier(entry)
            ON written_frontier.entry ->> 'streamId' = stored_frontier.entry ->> 'streamId'
        WHERE written_frontier.entry IS NULL
           OR (written_frontier.entry ->> 'nextOrdinal')::BIGINT < (stored_frontier.entry ->> 'nextOrdinal')::BIGINT) THEN
        RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint frontier is monotonic per stream and no stream may leave it (id=%).', OLD.id;
    END IF;

    -- EITHER axis is a claim, as 0132's and 0137's arms are: an owner swap that leaves the fence untouched is exactly
    -- how a live lease would otherwise be taken, so entering on the fence alone would be bypassable. Owner -> NULL is
    -- a RELEASE and stays outside; a same-owner expiry renewal touches neither axis and stays outside too.
    IF NEW.reducer_fence IS DISTINCT FROM OLD.reducer_fence
       OR (NEW.reducer_owner_id IS DISTINCT FROM OLD.reducer_owner_id AND NEW.reducer_owner_id IS NOT NULL) THEN
        IF OLD.reducer_lease_expires_at > clock_timestamp() THEN
            RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint live reducer lease cannot be reclaimed (id=%, fence=%, holder=%).', OLD.id, OLD.reducer_fence, OLD.reducer_owner_id;
        END IF;
        IF NEW.reducer_fence <> OLD.reducer_fence + 1 OR NEW.reducer_owner_id IS NULL
           OR NEW.reducer_lease_expires_at IS NULL OR NEW.reducer_lease_expires_at <= clock_timestamp() THEN
            RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint reducer claim must advance the fence exactly once with a live expiry (id=%, old=%, attempted=%).', OLD.id, OLD.reducer_fence, NEW.reducer_fence;
        END IF;
        IF NEW.position_jsonb IS DISTINCT FROM OLD.position_jsonb
           OR NEW.records_consumed IS DISTINCT FROM OLD.records_consumed
           OR NEW.reduced_state_jsonb IS DISTINCT FROM OLD.reduced_state_jsonb THEN
            RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint reducer claim cannot move the reduction (id=%).', OLD.id;
        END IF;
        -- Exhaustive: identity, revision, the frontier and the fence have all been proved for a claim.
        RETURN NEW;
    END IF;

    IF OLD.reducer_owner_id IS NOT NULL AND NEW.reducer_owner_id IS NULL THEN
        IF NEW.reducer_lease_expires_at IS NOT NULL
           OR NEW.position_jsonb IS DISTINCT FROM OLD.position_jsonb
           OR NEW.records_consumed IS DISTINCT FROM OLD.records_consumed
           OR NEW.reduced_state_jsonb IS DISTINCT FROM OLD.reduced_state_jsonb THEN
            RAISE EXCEPTION 'workflow_run_harness_reduction_checkpoint release hands back the lease only (id=%).', OLD.id;
        END IF;
        RETURN NEW;
    END IF;

    -- The only shape left is a plain ADVANCE, and this trigger deliberately does not try to fence it. It sees OLD and
    -- NEW, never WHICH session issued the statement, so a displaced reducer's advance is indistinguishable from the
    -- holder's. What is left standing is the frontier's monotonicity above — a resurrected reducer cannot rewind the
    -- row to its own older prefix — and the writer's own predicate, which is where holdership actually lives.
    --
    -- A column recording "the fence I hold" was deliberately NOT added: for any column an UPDATE omits, NEW carries
    -- the STORED value, so `NEW.advanced_under_fence <> OLD.reducer_fence` is satisfied by exactly the writer it
    -- would exist to refuse — it would read as protection and be none. 0137 carries no such column either and rests
    -- holdership entirely on that predicate.
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_harness_reduction_checkpoint_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_harness_reduction_checkpoint
    FOR EACH ROW EXECUTE FUNCTION workflow_run_harness_reduction_checkpoint_guard();

COMMENT ON COLUMN workflow_run_harness_reduction_checkpoint.reducer_kind IS
    'Immutable <kind>/vN of the reduction that produced this state; a changed state shape is a NEW kind, never a rewrite.';
COMMENT ON COLUMN workflow_run_harness_reduction_checkpoint.position_jsonb IS
    'The serialized reduction position: {"streams":[{"streamId","nextOrdinal"}]}, zero-based, monotonic, distinct, and no stream may leave it.';
COMMENT ON COLUMN workflow_run_harness_reduction_checkpoint.records_consumed IS
    'Exactly the frontier''s total AND the reduced state''s own recordsConsumed; the three can never disagree.';
COMMENT ON COLUMN workflow_run_harness_reduction_checkpoint.reducer_fence IS
    'Advances by exactly one on every ACQUISITION, and an acquisition over a still-live lease is refused, so each value is acquired by at most one owner. A release leaves it where it is. The trigger cannot tell who issued a statement: a holder proves itself with WHERE reducer_owner_id = <me> AND reducer_fence = <observed> AND revision = <observed>, and an advance carrying no such predicate is accepted here whoever sent it.';
