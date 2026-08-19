-- 0143_workflow_run_native_record_unparsed_channel.sql
--
-- Admits ONE new normalization state, narrows the index whose meaning that state would otherwise break, and reshapes
-- the attempt index so the one query this plane actually runs stops scanning the whole attempt. No table, column or
-- trigger changes.
--
-- WHY A NEW STATE. Until now every frame reaching this plane came off the harness's stdout, was handed to that
-- harness's parser, and recorded what the parser made of it: Projected, Unrecognized, or Failed. The slice shipping
-- with this script routes the harness's STDERR here as well, and it deliberately asks no parser anything about it — a
-- parser written against a harness's stdout protocol would read a diagnostic that happens to resemble a protocol frame
-- as an event, and project into the semantic stream something the harness never said.
--
-- Recording those frames as 'Unrecognized' would have avoided this script and asserted a falsehood: that state means
-- the parser yielded nothing, and no parser ran. 'NotParsed' is that distinction made storable — the frame is here, in
-- full, and nobody has claimed to have interpreted it.
--
-- WHY THE INDEX MOVES WITH IT. ix_workflow_run_native_record_unprojected exists to answer "which frames could we not
-- interpret", and 0139 spelled that as normalization <> 'Projected' because the only two non-Projected states were a
-- parse that found nothing and a parse that threw. A frame nobody attempted to interpret is not a frame that could not
-- be interpreted, so leaving the predicate alone would fill the answer with every diagnostic line every run ever wrote
-- and make the query useless at exactly the size where it matters. The new predicate names the two states that ARE the
-- question. It indexes strictly fewer rows than before and no reader loses a row it was entitled to: nothing reads
-- these tables yet, and no row that was Projected/Unrecognized/Failed changes state.
--
-- WHY THE ATTEMPT INDEX GAINS channel. The plane runs exactly ONE read query against this table: the head of an
-- attempt's records on ONE channel, MAX(source_offset_bytes + source_length_bytes) WHERE team_id/attempt_id/channel,
-- which is the position a resumed opening records above. Until this slice it ran only on the rare re-attach; the stderr
-- opening makes it run once per durable round. ix_workflow_run_native_record_attempt carried (team_id, attempt_id,
-- ingested_at, id) — no channel and neither offset — so the planner can only walk every index entry for the attempt and
-- heap-fetch each row to test the channel and read the offsets. At a round's terminal the attempt already holds one row
-- per stdout line of the whole round, so the cost of the question is linear in the frames the run recorded, on a chatty
-- agent tens of thousands of heap fetches per round, to answer a question that is 0 on a channel nothing has recorded.
-- With channel as the third key column the equality prefix is complete: the first stderr opening of an attempt touches
-- no entries at all, and a later one touches only that channel's. An attempt's records in ingest order remain
-- answerable on the leading (team_id, attempt_id) pair. Rows are unchanged either way — an index reshape loses nothing.
--
-- Each index is dropped and recreated rather than altered, because PostgreSQL has no ALTER INDEX ... SET PREDICATE and
-- no ALTER INDEX ... ADD COLUMN. The CHECK is dropped and re-added for the same reason; all of it is cheap here (the
-- plane is a dual write with no production readers), and the re-added constraint is the 0139 text plus one accepted
-- value, with its error-code arm unchanged — a NotParsed frame carries no error code, exactly like Projected and
-- Unrecognized.
--
-- Nothing here changes what an Agent Run resolves to. No column of this table is read by completion, terminal
-- decision, planner, oracle or model routing, and this script neither writes nor rewrites a single row.
-- Rollback: recreate ix_workflow_run_native_record_unprojected with WHERE normalization <> 'Projected', recreate
--           ix_workflow_run_native_record_attempt on (team_id, attempt_id, ingested_at, id), and re-add the CHECK
--           without 'NotParsed' (only safe once no row carries it).

ALTER TABLE workflow_run_native_record
    DROP CONSTRAINT ck_workflow_run_native_record_normalization;

ALTER TABLE workflow_run_native_record
    ADD CONSTRAINT ck_workflow_run_native_record_normalization CHECK (
        normalization IN ('Projected', 'Unrecognized', 'NotParsed', 'Failed')
        AND ((normalization = 'Failed' AND normalization_error_code IS NOT NULL AND btrim(normalization_error_code) <> '')
            OR (normalization <> 'Failed' AND normalization_error_code IS NULL AND normalization_error_message IS NULL)));

DROP INDEX ix_workflow_run_native_record_unprojected;

CREATE INDEX ix_workflow_run_native_record_unprojected
    ON workflow_run_native_record (team_id, execution_id, id)
    WHERE normalization IN ('Unrecognized', 'Failed');

DROP INDEX ix_workflow_run_native_record_attempt;

CREATE INDEX ix_workflow_run_native_record_attempt
    ON workflow_run_native_record (team_id, attempt_id, channel, ingested_at, id);

COMMENT ON COLUMN workflow_run_native_record.normalization IS
    'What the parser made of this frame. Unrecognized is the silent drop made durable: the parser yielded no event and the frame is still here. NotParsed means no parser was asked at all - the frame arrived on a channel this plane does not interpret (stderr), whose diagnostics a parser built for the harness stdout protocol would mis-read as events. Failed carries the REDACTED reason the parser threw; the throw itself is not contained, it propagates into the run exactly as it did before this plane existed.';
