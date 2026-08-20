-- 0148_workflow_run_data_manifest_advance.sql
--
-- 0146 gave the completeness plane its invariants and left ONE rule to whoever wrote a producer: take
-- workflow_run_data_completeness_lock EXPLICITLY, as the first statement of your own transaction, BEFORE you probe the
-- run's open gaps. 0146's own guards take that lock in a BEFORE ROW trigger, which fires after the INSERT's value
-- expressions have already been evaluated against the statement snapshot — so a producer that probes unlocked and then
-- writes has its whole statement refused when a gap commits in between. The first producer (the native-record facet)
-- got that right, and PROVED the rule is load-bearing by experiment: removing the one lock line lost the statement
-- entirely. What it could not do is stop the SECOND producer getting it wrong, because the rule lived in a doc-comment.
--
-- THIS MIGRATION MOVES THE RULE INSIDE THE DATABASE, so there is no order left for a producer to choose. The lock, the
-- gap probe and the manifest write become ONE function whose FIRST statement is the lock. A caller holding no lock, in
-- no transaction of its own, calling this function on a bare connection, is now correct — not because it remembered
-- anything, but because the sequence it could have got wrong is not reachable from outside. pg_advisory_xact_lock
-- cannot be released before its transaction ends, so a lock taken by the function's first statement is provably still
-- held by the INSERT that follows it and by the BEFORE ROW guard that re-probes underneath.
--
-- WHY THE GUARDS ARE LEFT EXACTLY AS 0146 WROTE THEM, rather than made to REFUSE a caller that arrives unlocked. Two
-- reasons, and both are about what the refusal would cost. (1) On the gap path a refusal is the inversion 0146 exists
-- to prevent: a gap INSERT's values come from the batch that failed, never from a probe, so the trigger taking the lock
-- is already the right time for it — and turning "you arrived unlocked" into a raise would make the honest observation
-- the thing that gets thrown away. (2) On the manifest path the guard's own PERFORM is what serializes every writer
-- that does NOT probe: a later reconciler, and the counter-example writers that pin 0146's own teeth in the test suite
-- by offering it dishonest rows directly. Requiring a lock the caller took itself would refuse all of them to catch a
-- mistake no caller can now make, because the only gap probe left in the system is the one inside the function below.
--
-- WHAT IS THEREFORE STILL UNENFORCED BY THE DATABASE, stated rather than implied: nothing here stops a future producer
-- hand-writing its own probing INSERT against workflow_run_data_manifest and skipping this function. What stops that is
-- a source-level pin in the unit suite (WorkflowRunDataCompletenessSchemaTests) asserting that no C# file under
-- backend/src spells the lock, the gap-count probe, or INSERT/UPDATE against the manifest table — every one of those
-- lives in SQL only. A pin is weaker than a constraint and is named as weaker.
--
-- THE OTHER HALF: WHICH DIRECTION A LOST STATEMENT ERRS IN. Both counts are deltas, so a statement lost to an
-- infrastructure failure is gone — it cannot be retried, because a COMMIT whose acknowledgement was lost would be
-- applied twice and a double-counted expectation reads present < expected FOREVER, turning a healthy run permanently
-- not-complete. Fail-closed would have become fail-always, so this function is deliberately NOT retryable and its
-- caller does not retry it. What makes the residue safe is the ORDER the producer advances in, which 0146 assumed and
-- the first producer did not have: an expectation DECLARED before its records land leaves present < expected if the
-- accounting after them is lost, which is not complete. Advancing both counts together instead leaves them equally
-- short and reads Exact over frames nobody counted. This function does not enforce that order (a delta cannot see the
-- write it describes); it makes the order expressible in one call, and the producer's own tests pin that it declares
-- first.
--
-- Rollback: DROP FUNCTION workflow_run_data_manifest_advance(UUID, UUID, TEXT, BIGINT, BIGINT, BOOLEAN, INTEGER);
--           DROP FUNCTION workflow_run_data_manifest_unstate_expectation(UUID, UUID, TEXT);
-- No table, index, constraint or trigger is touched.

-- Fold one delta into a facet's completeness statement, computing the verdict HERE so a producer never offers 0146 a
-- claim it would refuse: complete is proposed only over a determinate expectation that is fully present, with nothing
-- known-missing in this facet and no open gap anywhere in the run.
--
-- An expectation that is already NULL ABSORBS: NULL + n is NULL, so a run whose expectation was un-stated stays
-- indeterminate however many later deltas land, and its existing not-complete verdict is carried rather than
-- recomputed. A facet whose known-missing count a gap has raised stays Partial — nothing here recovers a gap, and a
-- later slice that does must lower this count itself rather than expect a raise to follow.
CREATE OR REPLACE FUNCTION workflow_run_data_manifest_advance(
    team UUID, run UUID, facet_name TEXT, expected_delta BIGINT, present_delta BIGINT, masked BOOLEAN, contract_version INTEGER) RETURNS void AS $$
DECLARE
    stamped_at TIMESTAMPTZ;
    open_here BIGINT;
    open_anywhere BOOLEAN;
BEGIN
    -- FIRST, and the whole reason this function exists. Everything below reads the gap plane; an xact lock cannot be
    -- released early, so from here to COMMIT no gap of this run can arrive between a probe and the write that used it.
    PERFORM workflow_run_data_completeness_lock(team, run);

    -- A delta that could go DOWN is a count a writer could walk back to complete. Recovery lowers known_missing_count
    -- and is a different operation with its own citation requirement; an advance only ever adds.
    IF expected_delta < 0 OR present_delta < 0 THEN
        RAISE EXCEPTION 'workflow_run_data_manifest advance is a non-negative delta (facet=%, expected_delta=%, present_delta=%). A count that can be walked down is a complete verdict a writer can reach by subtraction.', facet_name, expected_delta, present_delta;
    END IF;

    stamped_at := clock_timestamp();
    open_here := workflow_run_capture_gap_open_count(team, run, facet_name::varchar);
    SELECT EXISTS (SELECT 1 FROM workflow_run_capture_gap
                   WHERE team_id = team AND workflow_run_id = run AND resolution = 'Open') INTO open_anywhere;

    INSERT INTO workflow_run_data_manifest AS statement (
        id, team_id, workflow_run_id, facet, expected_record_count, present_record_count,
        known_missing_count, verdict, revision, schema_version, created_at, last_modified_at)
    VALUES (gen_random_uuid(), team, run, facet_name, expected_delta, present_delta, open_here,
            CASE WHEN open_anywhere OR open_here > 0 OR present_delta < expected_delta THEN 'Partial'
                 WHEN masked THEN 'RedactedExact' ELSE 'Exact' END,
            1, contract_version, stamped_at, stamped_at)
    ON CONFLICT (team_id, workflow_run_id, facet) DO UPDATE SET
        expected_record_count = statement.expected_record_count + expected_delta,
        present_record_count = statement.present_record_count + present_delta,
        known_missing_count = GREATEST(statement.known_missing_count, excluded.known_missing_count),
        verdict = CASE
            WHEN statement.expected_record_count IS NULL THEN statement.verdict
            WHEN excluded.verdict = 'Partial' THEN 'Partial'
            WHEN statement.present_record_count + present_delta < statement.expected_record_count + expected_delta THEN 'Partial'
            WHEN GREATEST(statement.known_missing_count, excluded.known_missing_count) > 0 THEN 'Partial'
            WHEN statement.verdict = 'RedactedExact' OR excluded.verdict = 'RedactedExact' THEN 'RedactedExact'
            ELSE 'Exact' END,
        revision = statement.revision + 1,
        last_modified_at = GREATEST(statement.last_modified_at, stamped_at);
END;
$$ LANGUAGE plpgsql;

-- The run's expectation stops being knowable for ONE facet: nobody could establish what should be there, so the
-- expectation is un-stated rather than rounded down to what happens to be present. 0146 refuses every complete verdict
-- over a NULL expectation, so this is how a facet fails closed for good.
--
-- Idempotent by its own predicate — a statement already indeterminate is left alone rather than re-revised — and it
-- invents nothing: a facet with no statement gets no row, because an absent statement is already the indeterminate
-- answer. Returns how many statements it revised, so a caller can log the run it happened to and stay silent otherwise.
CREATE OR REPLACE FUNCTION workflow_run_data_manifest_unstate_expectation(team UUID, run UUID, facet_name TEXT) RETURNS BIGINT AS $$
DECLARE
    revised BIGINT;
BEGIN
    -- Same reason, same position: the SET list below probes the gap plane, so the probe and the guard's re-probe have
    -- to see one set.
    PERFORM workflow_run_data_completeness_lock(team, run);

    UPDATE workflow_run_data_manifest SET
        expected_record_count = NULL,
        known_missing_count = GREATEST(known_missing_count, workflow_run_capture_gap_open_count(team_id, workflow_run_id, facet)),
        verdict = CASE WHEN GREATEST(known_missing_count, workflow_run_capture_gap_open_count(team_id, workflow_run_id, facet)) > 0
                       THEN 'Partial' ELSE 'LegacyUnknown' END,
        revision = revision + 1,
        last_modified_at = GREATEST(last_modified_at, clock_timestamp())
    WHERE team_id = team AND workflow_run_id = run AND facet = facet_name AND expected_record_count IS NOT NULL;

    GET DIAGNOSTICS revised = ROW_COUNT;

    RETURN revised;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION workflow_run_data_manifest_advance(UUID, UUID, TEXT, BIGINT, BIGINT, BOOLEAN, INTEGER) IS
    'The only way a producer advances a facet''s completeness statement. Takes the per-run rendezvous lock as its first statement, so the gap probe and the write it feeds cannot be separated by a committing gap. Callers need no transaction and no lock of their own.';
COMMENT ON FUNCTION workflow_run_data_manifest_unstate_expectation(UUID, UUID, TEXT) IS
    'Un-states one facet''s expectation (expected_record_count -> NULL), which 0146 refuses every complete verdict over. Takes the per-run rendezvous lock as its first statement for the same reason. Returns the number of statements revised.';
