-- 0139_workflow_run_native_record.sql
--
-- The LOSSLESS native-record plane and its semantic projection, keyed to the execution/attempt identity 0137 built.
-- Today the normalized agent_run_event log is the ONLY durable interpretation of a harness's output, and it is lossy
-- by construction: IAgentHarness.ParseEvents returns an empty list for any line it does not recognise, so a native
-- frame class the adapter never learned leaves NO row at all. The raw bytes survive only in a spool that ages out.
-- A native record is the floor underneath that: the frame lands as its own durable row, and a semantic event is a
-- PROJECTION of one or more records that never replaces them.
--
-- WHAT "LOSSLESS" CLAIMS HERE, AND WHAT IT DOES NOT. It does NOT claim byte-for-byte native fidelity: what lands is
-- the REDACTED frame, because unredacted secrets must never reach storage. This follows 0133's discipline exactly —
-- source_offset_bytes/source_length_bytes describe the RAW frame's geometry in its stream while digest/size_bytes
-- describe the CAPTURED (redacted) bytes, so how much redaction changed is a computable fact rather than a lost one,
-- and `redaction` states which of the two the payload is. A frame whose bytes were masked is Masked and can never be
-- read as verbatim. What IS claimed: every frame delivered to the capture pump gets a row, whatever the parser then
-- makes of it, and the row's payload is never edited afterwards.
--
-- ORDERING. The frame is captured, then parsing is attempted, and the record carries the OUTCOME of that attempt in
-- `normalization`: Projected (the parser yielded events), Unrecognized (it yielded none — the silent drop, now
-- durable and countable), or Failed (it threw, with the reason). Both tables are strictly append-only, so the marker
-- is decided at insert and the payload can never be rewritten to match a later interpretation.
--
-- INVARIANTS, all enforced here rather than by a convention a writer may forget:
--   * ordinals contiguous from zero WITHIN a stream — the predecessor must exist, checked by a DEFERRED constraint
--     trigger so the rule holds for any writer in any insert order (see the note on that trigger), and backed by a
--     unique index that two racing appenders cannot both pass.
--   * a record carries inline payload XOR an artifact ref — never both, never neither, so "no payload" can never be
--     silently read as an empty frame.
--   * a semantic event names at least one source native record. This is STRICTER than AgentSemanticEventV1.Validate(),
--     which tolerates an ungrounded event as long as it claims no exactness; in this plane every event is projected
--     from a frame, so zero sources is never honest. It therefore also refuses the load-bearing case — an Exact claim
--     with no source record, i.e. a claim about nothing.
--   * every named source record must EXIST, in the same tenant and execution. An array cannot carry a foreign key, so
--     the guard is the referential integrity here.
--   * an Exact projection's sources must be verbatim, and a RedactedExact projection's sources must have been
--     captured at all. NativeRecordV1 already refuses a Masked or Withheld frame carrying an Exact payload claim; a
--     database that let a semantic event claim Exact over those same bytes would simply move the lie one table over.
--
-- OWNERSHIP. Both tables hang off the harness EXECUTION via 0137's ak_workflow_run_harness_execution_scope, which
-- carries the tenant and the Agent Run with it — so a record can never be attributed to an execution of another
-- team's run. attempt_id rides as the same guard-proved soft correlation 0137 uses for workflow_run_id: the attempt
-- table exposes no (team_id, id) key to reference, and widening another slice's table as a rider is not this one's
-- business, so the trigger proves the attempt belongs to this exact execution instead.
--
-- ISOLATION, which is what makes the next sentence true rather than hoped for. Capture writes on its OWN connection
-- and its OWN unit of work, never the Agent Run's: a refused write must not be able to leave rows staged in the
-- tracker that the run's very next save replays, which would turn a refused frame into a failed run. The refusals
-- below are reachable by design (0137 rejects a superseded worker's fence on exactly the reclaim-for-reattach case),
-- so this is a load-bearing property of the writer, not a defensive nicety.
--
-- WHAT THIS SLICE DOES NOT DO, stated so a reader does not infer it from the schema. It never TERMINALIZES an
-- execution: the writer opens one and re-enters it, and 0137's attempt-head arm forces state='Running', so after a
-- run ends its execution row is left Running — stale rather than false at insert, and exactly the population 0137's
-- own header says an age-scan over ix_workflow_run_harness_execution_stale_live must Abandon. That sweeper is not
-- here. Neither is closing the attempt of a worker that died mid-round: capture is not opened on the re-attach path,
-- so nobody closes what the dead worker left Running.
--
-- Nothing READS these tables yet. The agent_run_event log and AgentRunResult keep their present semantics; this is a
-- dual write beside them and cannot change what an Agent Run resolves to.
-- Rollback: DROP TABLE workflow_run_semantic_event; DROP TABLE workflow_run_native_record;

CREATE TABLE workflow_run_native_record (
    id                          UUID          NOT NULL PRIMARY KEY,
    team_id                     UUID          NOT NULL,
    agent_run_id                UUID          NOT NULL,
    execution_id                UUID          NOT NULL,
    attempt_id                  UUID          NOT NULL,
    stream_id                   UUID          NOT NULL,
    ordinal                     BIGINT        NOT NULL,
    channel                     VARCHAR(24)   NOT NULL,
    native_type                 VARCHAR(255)  NOT NULL,
    native_schema               VARCHAR(255)  NULL,
    native_schema_version       VARCHAR(64)   NULL,
    occurred_at                 TIMESTAMPTZ   NULL,
    ingested_at                 TIMESTAMPTZ   NOT NULL,
    source_offset_bytes         BIGINT        NOT NULL,
    source_length_bytes         BIGINT        NOT NULL,
    inline_payload              TEXT          NULL,
    payload_ref_jsonb           JSONB         NULL,
    digest_algorithm            VARCHAR(32)   NOT NULL,
    digest                      VARCHAR(64)   NOT NULL,
    size_bytes                  BIGINT        NOT NULL,
    payload_encoding            VARCHAR(16)   NOT NULL,
    redaction                   VARCHAR(16)   NOT NULL,
    is_final                    BOOLEAN       NOT NULL,
    normalization               VARCHAR(24)   NOT NULL,
    normalization_error_code    VARCHAR(128)  NULL,
    normalization_error_message VARCHAR(2048) NULL,
    contract_version            INTEGER       NOT NULL,
    created_at                  TIMESTAMPTZ   NOT NULL,

    CONSTRAINT fk_workflow_run_native_record_execution FOREIGN KEY (team_id, execution_id, agent_run_id)
        REFERENCES workflow_run_harness_execution (team_id, id, agent_run_id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_native_record_bounds CHECK (
        ordinal >= 0 AND source_offset_bytes >= 0 AND source_length_bytes >= 0 AND size_bytes >= 0
        AND contract_version > 0 AND btrim(native_type) <> ''
        AND (native_schema IS NULL OR btrim(native_schema) <> '')
        AND (native_schema_version IS NULL OR btrim(native_schema_version) <> '')),
    CONSTRAINT ck_workflow_run_native_record_channel CHECK (
        channel IN ('Stdout', 'Stderr', 'Protocol', 'Control', 'SessionState', 'ModelWire', 'ToolWire', 'Hook', 'Metric', 'Debug')),
    CONSTRAINT ck_workflow_run_native_record_digest CHECK (
        digest_algorithm = 'sha256/v1' AND digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_workflow_run_native_record_encoding CHECK (
        payload_encoding IN ('Utf8', 'Base64')),
    -- The XOR the contract type states: exactly one payload arm, so a reader can never take an absent payload for an
    -- empty frame. Written as a boolean inequality because that is the only spelling that refuses BOTH mistakes.
    CONSTRAINT ck_workflow_run_native_record_payload CHECK (
        (inline_payload IS NULL) <> (payload_ref_jsonb IS NULL)
        AND (payload_ref_jsonb IS NULL OR jsonb_typeof(payload_ref_jsonb) = 'object')),
    -- Mirrors NativeRecordV1's own rule: a frame that was deliberately never captured has metadata only, so its
    -- payload must be a reference to unavailable content and can never be inline bytes.
    CONSTRAINT ck_workflow_run_native_record_redaction CHECK (
        redaction IN ('None', 'Masked', 'Withheld')
        AND (redaction <> 'Withheld' OR inline_payload IS NULL)),
    CONSTRAINT ck_workflow_run_native_record_normalization CHECK (
        normalization IN ('Projected', 'Unrecognized', 'Failed')
        AND ((normalization = 'Failed' AND normalization_error_code IS NOT NULL AND btrim(normalization_error_code) <> '')
            OR (normalization <> 'Failed' AND normalization_error_code IS NULL AND normalization_error_message IS NULL))),
    CONSTRAINT ck_workflow_run_native_record_time CHECK (created_at >= ingested_at)
);

-- The concurrency backstop for contiguity: in one session the guard refuses a gap first, but two appenders racing
-- past their own snapshots each see the predecessor and neither sees the other's ordinal — only the index does.
CREATE UNIQUE INDEX ux_workflow_run_native_record_ordinal
    ON workflow_run_native_record (team_id, stream_id, ordinal);
CREATE INDEX ix_workflow_run_native_record_attempt
    ON workflow_run_native_record (team_id, attempt_id, ingested_at, id);
CREATE INDEX ix_workflow_run_native_record_execution
    ON workflow_run_native_record (team_id, execution_id, ingested_at, id);
-- The whole point of the plane, as a query: which frames could not be interpreted. Partial, because the answer is
-- rare and an index over every projected frame would grow with the run.
CREATE INDEX ix_workflow_run_native_record_unprojected
    ON workflow_run_native_record (team_id, execution_id, id)
    WHERE normalization <> 'Projected';

CREATE TABLE workflow_run_semantic_event (
    id                        UUID          NOT NULL PRIMARY KEY,
    team_id                   UUID          NOT NULL,
    agent_run_id              UUID          NOT NULL,
    execution_id              UUID          NOT NULL,
    source_native_record_ids  UUID[]        NOT NULL,
    event_type                VARCHAR(512)  NOT NULL,
    event_schema_version      INTEGER       NOT NULL,
    session_id                UUID          NULL,
    turn_id                   UUID          NULL,
    step_id                   UUID          NULL,
    model_call_id             UUID          NULL,
    tool_call_id              UUID          NULL,
    correlation_id            UUID          NULL,
    causation_id              UUID          NULL,
    necessity                 VARCHAR(16)   NOT NULL,
    projection_quality        VARCHAR(24)   NOT NULL,
    payload_ref_jsonb         JSONB         NULL,
    contract_version          INTEGER       NOT NULL,
    projected_at              TIMESTAMPTZ   NOT NULL,
    created_at                TIMESTAMPTZ   NOT NULL,

    CONSTRAINT fk_workflow_run_semantic_event_execution FOREIGN KEY (team_id, execution_id, agent_run_id)
        REFERENCES workflow_run_harness_execution (team_id, id, agent_run_id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_semantic_event_bounds CHECK (
        event_schema_version > 0 AND contract_version > 0
        AND event_type ~ '^[a-zA-Z][a-zA-Z0-9+.-]*:'
        AND (payload_ref_jsonb IS NULL OR jsonb_typeof(payload_ref_jsonb) = 'object')
        AND created_at >= projected_at),
    -- COALESCE is load-bearing, not decoration: array_length('{}'::UUID[], 1) is NULL, and a CHECK that evaluates to
    -- NULL is SATISFIED — so the naive `array_length(...) >= 1` accepts exactly the empty array it exists to refuse.
    CONSTRAINT ck_workflow_run_semantic_event_grounding CHECK (
        COALESCE(array_length(source_native_record_ids, 1), 0) >= 1
        AND array_ndims(source_native_record_ids) = 1
        AND array_position(source_native_record_ids, NULL::UUID) IS NULL
        AND array_position(source_native_record_ids, '00000000-0000-0000-0000-000000000000'::UUID) IS NULL),
    CONSTRAINT ck_workflow_run_semantic_event_vocabulary CHECK (
        necessity IN ('Required', 'Ignorable')
        AND projection_quality IN ('Exact', 'RedactedExact', 'Derived', 'Heuristic', 'Unknown'))
);

CREATE INDEX ix_workflow_run_semantic_event_execution
    ON workflow_run_semantic_event (team_id, execution_id, projected_at, id);
-- Reading an event's grounding back is a containment question over the array, which only an inverted index answers.
CREATE INDEX ix_workflow_run_semantic_event_sources
    ON workflow_run_semantic_event USING GIN (source_native_record_ids);
CREATE INDEX ix_workflow_run_semantic_event_qualified
    ON workflow_run_semantic_event (team_id, execution_id, id)
    WHERE projection_quality NOT IN ('Exact', 'RedactedExact');

CREATE OR REPLACE FUNCTION workflow_run_native_record_guard() RETURNS trigger AS $$
DECLARE
    attempt_execution_id UUID;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'workflow_run_native_record is an append-only capture floor — % rejected (id=%). A frame is captured once; a later interpretation of it is a new semantic event, never an edit of the bytes.', TG_OP, OLD.id;
    END IF;

    -- The composite foreign key already proves the execution's tenant and Agent Run. What it cannot prove is that the
    -- attempt named here is a process OF THAT execution rather than of another one in the same run.
    SELECT execution_id INTO attempt_execution_id FROM workflow_run_harness_process_attempt
    WHERE team_id = NEW.team_id AND id = NEW.attempt_id AND agent_run_id = NEW.agent_run_id
    FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow_run_native_record requires its tenant-bound process attempt (attempt_id=%).', NEW.attempt_id;
    END IF;
    IF attempt_execution_id <> NEW.execution_id THEN
        RAISE EXCEPTION 'workflow_run_native_record attempt must belong to its own execution (attempt_id=%, attempt_execution=%, claimed=%).', NEW.attempt_id, attempt_execution_id, NEW.execution_id;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_native_record_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_native_record
    FOR EACH ROW EXECUTE FUNCTION workflow_run_native_record_guard();

-- Contiguity is a TRANSACTION-level invariant, so it is a DEFERRED constraint trigger — the same mechanism a foreign
-- key uses, for the same reason. PostgreSQL's visibility rule for a function invoked BY a statement is that it cannot
-- see that statement's own rows, so a BEFORE arm asking "does ordinal-1 exist?" would refuse every row but the first
-- of a batched insert; and a plain AFTER ROW arm only sees the rows of its OWN statement, so it would still refuse a
-- batch whose writer emitted ordinals across statements or out of order (EF Core sorts same-table inserts by primary
-- key, which here is a random GUID). Deferring to commit makes the rule hold for any writer in any order — which is
-- what "contiguous" actually means — at the cost of the violation surfacing from the commit rather than the insert.
CREATE OR REPLACE FUNCTION workflow_run_native_record_enforce_contiguity() RETURNS trigger AS $$
BEGIN
    IF NEW.ordinal = 0 THEN
        RETURN NULL;
    END IF;

    PERFORM 1 FROM workflow_run_native_record
    WHERE team_id = NEW.team_id AND stream_id = NEW.stream_id AND ordinal = NEW.ordinal - 1;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow_run_native_record ordinals are contiguous from zero within a stream (stream_id=%, attempted=%). A gap is an unrecorded frame, which is exactly what this plane exists to make impossible.', NEW.stream_id, NEW.ordinal;
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER workflow_run_native_record_enforce_stream_contiguity
    AFTER INSERT ON workflow_run_native_record
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION workflow_run_native_record_enforce_contiguity();

CREATE OR REPLACE FUNCTION workflow_run_semantic_event_guard() RETURNS trigger AS $$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'workflow_run_semantic_event is an append-only projection — % rejected (id=%). A projection that changed its mind is a new event citing the same frames, so the old reading stays auditable.', TG_OP, OLD.id;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER workflow_run_semantic_event_enforce_invariants
    BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_semantic_event
    FOR EACH ROW EXECUTE FUNCTION workflow_run_semantic_event_guard();

-- Grounding is DEFERRED for the same reason contiguity is, and for one more: a projection and the frame it cites are
-- written in the SAME transaction, and nothing orders two different tables' inserts within it — an immediate arm would
-- make a legal batch legal or illegal depending on which table a writer happened to insert first.
CREATE OR REPLACE FUNCTION workflow_run_semantic_event_enforce_grounding() RETURNS trigger AS $$
DECLARE
    grounded_count INTEGER;
    unverbatim_count INTEGER;
BEGIN
    -- An array column cannot carry a foreign key, so this IS the referential integrity of a projection's grounding.
    -- Counting the matches (rather than testing each id) makes "one of these five does not exist" a single query.
    SELECT count(*) INTO grounded_count FROM workflow_run_native_record
    WHERE team_id = NEW.team_id AND execution_id = NEW.execution_id
      AND id = ANY (NEW.source_native_record_ids);
    IF grounded_count <> COALESCE(array_length(NEW.source_native_record_ids, 1), 0) THEN
        RAISE EXCEPTION 'workflow_run_semantic_event must cite native records of its own execution (execution_id=%, cited=%, found=%).', NEW.execution_id, COALESCE(array_length(NEW.source_native_record_ids, 1), 0), grounded_count;
    END IF;

    IF NEW.projection_quality NOT IN ('Exact', 'RedactedExact') THEN
        RETURN NULL;
    END IF;

    -- Exactness is a claim about BYTES, so it can never outrun the bytes actually captured. Exact needs every source
    -- verbatim; RedactedExact tolerates masking (that is what it means) but not a frame nobody captured.
    SELECT count(*) INTO unverbatim_count FROM workflow_run_native_record
    WHERE team_id = NEW.team_id AND execution_id = NEW.execution_id
      AND id = ANY (NEW.source_native_record_ids)
      AND ((NEW.projection_quality = 'Exact' AND redaction <> 'None')
        OR (NEW.projection_quality = 'RedactedExact' AND redaction = 'Withheld'));
    IF unverbatim_count > 0 THEN
        RAISE EXCEPTION 'workflow_run_semantic_event cannot claim % over % source frame(s) that were masked or never captured (execution_id=%).', NEW.projection_quality, unverbatim_count, NEW.execution_id;
    END IF;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER workflow_run_semantic_event_enforce_source_grounding
    AFTER INSERT ON workflow_run_semantic_event
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION workflow_run_semantic_event_enforce_grounding();

COMMENT ON TABLE workflow_run_native_record IS
    'One losslessly captured native frame of a harness process. Append-only. The payload is the REDACTED frame; source_offset_bytes/source_length_bytes describe the raw frame while digest/size_bytes describe the captured bytes.';
COMMENT ON TABLE workflow_run_semantic_event IS
    'The normalized PROJECTION of one or more native records. Append-only, and never a substitute: source_native_record_ids keeps the exact frames it was folded from.';
COMMENT ON COLUMN workflow_run_native_record.ordinal IS
    'Zero-based position within stream_id, contiguous. Ordering is per stream, never global — a global sequence would force every channel through one writer.';
COMMENT ON COLUMN workflow_run_native_record.source_length_bytes IS
    'Byte length of the RAW frame in its stream. It differs from size_bytes exactly when redaction changed the bytes, which is how much redaction cost.';
COMMENT ON COLUMN workflow_run_native_record.source_offset_bytes IS
    'Offset of the RAW frame in its stream. A per-stream cursor derived from the frames as delivered, NOT a byte-exact index into a spool file — a resume reads the log-capture plane committed source head, never this.';
COMMENT ON COLUMN workflow_run_native_record.redaction IS
    'How the captured bytes relate to the wire. Masked means secret spans were replaced before capture, so the bytes differ from the wire and can never be read as verbatim.';
COMMENT ON COLUMN workflow_run_native_record.normalization IS
    'What the parser made of this frame. Unrecognized is the silent drop made durable: the parser yielded no event and the frame is still here. Failed carries the REDACTED reason the parser threw; the throw itself is not contained, it propagates into the run exactly as it did before this plane existed.';
COMMENT ON COLUMN workflow_run_native_record.attempt_id IS
    'The physical process that produced this frame. Soft correlation proved by the guard, because the attempt table exposes no tenant-scoped key to reference and widening it is another slice business.';
COMMENT ON COLUMN workflow_run_semantic_event.source_native_record_ids IS
    'The native records this event was folded from, in source order. At least one, always — stricter than AgentSemanticEventV1, which tolerates an ungrounded non-exact event; in this plane every event is a projection of a frame.';
COMMENT ON COLUMN workflow_run_semantic_event.projection_quality IS
    'How faithfully this event represents its sources. Only Exact and RedactedExact may back a strict read, and the guard refuses either over sources that were masked or never captured.';
