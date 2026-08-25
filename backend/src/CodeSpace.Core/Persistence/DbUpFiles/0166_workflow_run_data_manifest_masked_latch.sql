-- 0166_workflow_run_data_manifest_masked_latch.sql
--
-- 0146 gave the manifest TWO complete verdicts, and the difference between them is a claim about the BYTES: Exact says
-- the stored record is verbatim, RedactedExact says it is whole but has masked spans in it. 0148 then made the verdict
-- a function of the deltas a producer folds in, and carried "something was masked" across those folds in the VERDICT
-- COLUMN ITSELF — `WHEN statement.verdict = 'RedactedExact' ... THEN 'RedactedExact'`.
--
-- THAT IS NOT A LATCH, because the verdict column is overwritten on the way past. Every honest Partial erases it. The
-- shortest real sequence, which is just two ordinary batches of a run whose first batch was masked:
--
--   1. declare(expected 2, present 0, masked f) -> Partial          (present < expected: correct, and not yet masked)
--   2. account (expected 0, present 2, masked t) -> RedactedExact   (the masked observation, held only here)
--   3. declare(expected 2, present 0, masked f) -> Partial          <-- correct verdict, and the ONLY memory of the
--                                                                       masking is now gone
--   4. account (expected 0, present 2, masked f) -> Exact           <-- reads verbatim over a run holding masked frames
--
-- The existing pin could not see it: it writes ONE batch and never advances the facet a second time. A run with five
-- masked frames followed by any unmasked batch ends up claiming bytes it does not have.
--
-- WHY THE FIX IS A COLUMN AND NOT A CAREFULLER CASE. The masked observation is a fact about the run's history, and the
-- verdict is a fact about its current counts; storing the first inside the second means every legitimate recomputation
-- of the second destroys the first. It also cannot be carried by the producer instead: C# would have to READ the run's
-- statement and then write, and 0148 exists precisely because a probe outside the per-run rendezvous lock is a
-- statement the guard refuses — and a refused delta is lost for good. So the latch lives in the same function, under
-- the same lock, in a column of its own.
--
-- masked_observed IS MONOTONIC BY CONSTRUCTION: the only statement that writes it is the ON CONFLICT arm below, and it
-- writes `statement.masked_observed OR excluded.masked_observed`, which cannot go from true back to false. Un-stating
-- an expectation does not touch it, and neither does the gap plane's downgrade — both of those change what the run is
-- known to be MISSING, never what it was seen to have masked.
--
-- ITS READER IS THIS FUNCTION. The column is not display metadata and is deliberately not mapped in EF: it exists so
-- that the verdict CASE two calls later can still tell that the run's bytes are not verbatim. Nothing else reads it,
-- and nothing else should - a caller that wants the answer reads the verdict, which is what the latch keeps correct.
--
-- BACKFILL, and what it can and cannot recover. Existing rows default to FALSE. For a row currently reading
-- RedactedExact that is exactly right, so it is seeded from the verdict. For a row whose masking was ALREADY erased by
-- this defect the evidence is gone — the deltas were folded in and are not stored — so those rows stay FALSE and keep
-- reading Exact. That is stated rather than papered over: this migration stops the loss, it cannot undo it, which is
-- the general property of a counter plane and the reason the defect was worth fixing before the table gets a reader.
--
-- Rollback: ALTER TABLE workflow_run_data_manifest DROP COLUMN masked_observed;
--           and restore workflow_run_data_manifest_advance from 0148.
-- No constraint, index or trigger is touched.

ALTER TABLE workflow_run_data_manifest
    ADD COLUMN IF NOT EXISTS masked_observed BOOLEAN NOT NULL DEFAULT FALSE;

-- The one recoverable case: a statement still READING RedactedExact has not been erased yet, so its own verdict is
-- honest evidence that this facet saw masked content.
UPDATE workflow_run_data_manifest SET masked_observed = TRUE WHERE verdict = 'RedactedExact';

COMMENT ON COLUMN workflow_run_data_manifest.masked_observed IS
    'Whether any record folded into this facet reached storage masked. Latched: set by workflow_run_data_manifest_advance and never cleared, because the verdict column it used to be carried in is overwritten by every legitimate Partial. Read by that same function to choose RedactedExact over Exact.';

-- Unchanged from 0148 except for the latch: the lock is still the first statement, the gap probes still run under it,
-- and the delta arithmetic is identical. What differs is that the redacted arm is now chosen from masked_observed
-- rather than from a verdict a Partial may have overwritten in between.
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
        known_missing_count, verdict, masked_observed, revision, schema_version, created_at, last_modified_at)
    VALUES (gen_random_uuid(), team, run, facet_name, expected_delta, present_delta, open_here,
            CASE WHEN open_anywhere OR open_here > 0 OR present_delta < expected_delta THEN 'Partial'
                 WHEN masked THEN 'RedactedExact' ELSE 'Exact' END,
            masked, 1, contract_version, stamped_at, stamped_at)
    ON CONFLICT (team_id, workflow_run_id, facet) DO UPDATE SET
        expected_record_count = statement.expected_record_count + expected_delta,
        present_record_count = statement.present_record_count + present_delta,
        known_missing_count = GREATEST(statement.known_missing_count, excluded.known_missing_count),
        -- The latch. Monotonic: a run whose bytes were ever masked can never read back as verbatim, however many
        -- unmasked batches follow and however many Partial verdicts pass over the row in between.
        masked_observed = statement.masked_observed OR excluded.masked_observed,
        verdict = CASE
            WHEN statement.expected_record_count IS NULL THEN statement.verdict
            WHEN excluded.verdict = 'Partial' THEN 'Partial'
            WHEN statement.present_record_count + present_delta < statement.expected_record_count + expected_delta THEN 'Partial'
            WHEN GREATEST(statement.known_missing_count, excluded.known_missing_count) > 0 THEN 'Partial'
            WHEN statement.masked_observed OR excluded.masked_observed THEN 'RedactedExact'
            ELSE 'Exact' END,
        revision = statement.revision + 1,
        last_modified_at = GREATEST(statement.last_modified_at, stamped_at);
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION workflow_run_data_manifest_advance(UUID, UUID, TEXT, BIGINT, BIGINT, BOOLEAN, INTEGER) IS
    'The only way a producer advances a facet''s completeness statement. Takes the per-run rendezvous lock as its first statement, so the gap probe and the write it feeds cannot be separated by a committing gap. Callers need no transaction and no lock of their own. A masked observation latches in masked_observed, so a run whose bytes were ever masked never reads back as verbatim.';
