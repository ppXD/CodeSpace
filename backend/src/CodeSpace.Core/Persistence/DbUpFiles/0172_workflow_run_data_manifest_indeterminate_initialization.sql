-- 0172_workflow_run_data_manifest_indeterminate_initialization.sql
--
-- 0171 initializes every registered producer facet of a starting run, and it does so with a DETERMINATE ZERO under a
-- COMPLETE verdict: expected_record_count 0, present_record_count 0, verdict 'Exact'. The column's own definition
-- refuses exactly that reading - it "is nullable rather than defaulted to zero precisely because zero is a determinate
-- claim ... and reading an unknown as that claim is the assurance this plane exists to refuse". So a run that reached
-- the engine's Enqueued->Running CAS and then died in bootstrap, before any producer counted anything, ends up with
-- four statements that each read COMPLETE, and the reader folds them into a run-wide Exact because its gate ("every
-- required facet has a statement") is satisfied by the INITIALIZER rather than by producers. The operator is told the
-- record is complete and verbatim for a run that captured nothing. Before 0171 the same run had no statements at all,
-- no run-wide verdict, and a visible "unstated required facets" warning.
--
-- WHAT THE INITIALIZER IS FOR, AND IS KEPT. Its value is that a facet's row exists from the moment the run starts, so
-- a producer that died before its first advance is distinguishable from a plane that never ran. That value does not
-- require a claim. This migration keeps the row and makes it INDETERMINATE: expected_record_count NULL, verdict
-- 'LegacyUnknown' - or 'Partial' when the run already holds a known-missing span of that facet, which is the same
-- choice workflow_run_data_manifest_unstate_expectation makes between two honest not-complete answers.
-- ck_workflow_run_data_manifest_completeness then refuses every complete verdict over the row, and the reader's fold
-- returns LegacyUnknown, which is how "nobody established what should be here" is spelled.
--
-- WHY THAT NEEDS A COLUMN, which is the part that is not obvious. 0148 made a NULL expectation ABSORB: `expected + d`
-- is NULL for every later delta and the verdict CASE carries the existing verdict rather than recomputing it. That is
-- load-bearing for UN-STATING - a producer that gave up may not have its facet walked back to complete by a later
-- partial count. It is WRONG for a facet nobody has stated yet: with the initializer writing NULL and nothing else
-- changed, every facet of every run would absorb its producers' deltas and stay LegacyUnknown forever, which is not a
-- fix but the plane switched off. "Never declared" and "declared and then un-stated" are different facts that 0148
-- represents identically, and no existing column separates them - revision cannot, because the gap plane's downgrade
-- advances an initializer row's revision without any producer having spoken.
--
-- expectation_declared IS THAT SEPARATION, and it is monotonic like masked_observed (0166): FALSE only on a statement
-- the initializer minted, latched TRUE by the first positive expectation delta and by every un-stating, never cleared.
-- Its DEFAULT IS TRUE, which is the conservative reading for every row that predates this migration: their NULL
-- expectations are un-statings, and they must keep absorbing. Its readers are workflow_run_data_manifest_advance and
-- workflow_run_data_manifest_unstate_expectation below; nothing else reads it and nothing else should, because a
-- caller that wants to know whether the facet is determinate reads expected_record_count, which these two keep honest.
--
-- WHAT THIS ALSO CLOSES, stated because it is a behaviour change and not only a repair. On a facet the initializer
-- minted, a present-only advance (expected_delta 0) can no longer manufacture a determinate expectation of zero out
-- of nothing: the expectation stays NULL until something positive is declared. Producers already avoided that by
-- never accounting for a batch whose declaration was lost; now the database avoids it too. A facet with NO row - one
-- outside the registered set - still takes the INSERT arm and still writes a determinate expected_delta, unchanged.
--
-- EXISTING ROWS. The corrective UPDATE below rewrites the statements 0171 already wrote. It identifies them by BOTH
-- counts being zero, and that is sound rather than heuristic: every production advance moves exactly one count
-- strictly above zero (a declaration is expected>0/present=0, an accounting is expected=0/present>0 - see
-- NativeRecordPlane.Completeness / .ProcessCompleteness / .ExecutionCompleteness and RecordingLLMClientDecorator), and
-- 0148 refuses a negative delta, so neither count can be walked back down. A row holding zero and zero has therefore
-- never been folded by a producer and can only have come from the initializer. The rewrite errs toward indeterminate
-- in any case: it removes a claim, it never removes a statement, and a facet whose producer later advances it
-- establishes its expectation normally.
--
-- WHAT THE REWRITE COSTS TO APPLY, stated because it is not free. Every UPDATE to this table fires 0146's BEFORE ROW
-- guard, which takes the per-run rendezvous lock, and DbUp runs the whole upgrade in ONE transaction - so the rewrite
-- holds one advisory-lock slot per DISTINCT RUN it touches until the migration commits. That set is bounded by the
-- runs that started while 0171 was deployed and this migration was not, which is small by construction. The guard is
-- deliberately NOT disabled to avoid it: another pod on the old image may be committing gaps while this runs, and the
-- guard's re-probe under that same lock is the only thing that stops a rewritten row landing below its facet's
-- known-missing floor. A database that somehow accumulated enough such runs to exhaust
-- max_locks_per_transaction x max_connections must run the UPDATE below in run-sized batches before deploying.
--
-- Rollback: ALTER TABLE workflow_run_data_manifest DROP COLUMN expectation_declared;
--           and restore all three functions from 0166 (advance), 0148 (unstate) and 0171 (initialize).
-- The rewritten rows are NOT restorable - the zeros they claimed were never evidence of anything.
-- No table, constraint, index or trigger is touched.

ALTER TABLE workflow_run_data_manifest
    ADD COLUMN IF NOT EXISTS expectation_declared BOOLEAN NOT NULL DEFAULT TRUE;

COMMENT ON COLUMN workflow_run_data_manifest.expectation_declared IS
    'Whether any producer has ever declared an expectation for this facet. Latched TRUE by workflow_run_data_manifest_advance on a positive expected delta and by workflow_run_data_manifest_unstate_expectation, never cleared; FALSE only on a statement workflow_run_data_manifest_initialize minted. Read by those same functions, because it is what separates an expectation nobody has stated yet - which the next declaration establishes - from one that was UN-STATED, which absorbs every later delta. Rows predating it default TRUE, the conservative reading.';

-- The rows 0171 already wrote, made indeterminate. Revision advances because every write to this table advances it
-- (the guard refuses anything else), and known_missing_count is reconciled in the same statement because the guard's
-- floor is re-probed on UPDATE and a gap may have arrived since the row was minted.
UPDATE workflow_run_data_manifest AS initialized SET
    expected_record_count = NULL,
    expectation_declared = FALSE,
    known_missing_count = GREATEST(initialized.known_missing_count,
        workflow_run_capture_gap_open_count(initialized.team_id, initialized.workflow_run_id, initialized.facet)),
    verdict = CASE WHEN GREATEST(initialized.known_missing_count,
                         workflow_run_capture_gap_open_count(initialized.team_id, initialized.workflow_run_id, initialized.facet)) > 0
                   THEN 'Partial' ELSE 'LegacyUnknown' END,
    revision = initialized.revision + 1,
    last_modified_at = GREATEST(initialized.last_modified_at, clock_timestamp())
WHERE initialized.expected_record_count = 0 AND initialized.present_record_count = 0;

-- Unchanged from 0171 except for what the statement SAYS: the lock is still the first thing the function does, the
-- gap probe still runs under it, and a replay still leaves an existing statement exactly as it found it. What differs
-- is that the minted row declares the facet EXISTS and states nothing about it, instead of declaring it empty.
CREATE OR REPLACE FUNCTION workflow_run_data_manifest_initialize(
    team UUID, run UUID, facets TEXT[], contract_version INTEGER) RETURNS BIGINT AS $$
DECLARE
    stamped_at TIMESTAMPTZ;
    inserted BIGINT;
BEGIN
    PERFORM workflow_run_data_completeness_lock(team, run);
    stamped_at := clock_timestamp();

    INSERT INTO workflow_run_data_manifest (
        id, team_id, workflow_run_id, facet, expected_record_count, present_record_count,
        known_missing_count, verdict, masked_observed, expectation_declared, revision, schema_version, created_at, last_modified_at)
    SELECT gen_random_uuid(), team, run, facet_name, NULL::BIGINT, 0, gaps.open_here,
           CASE WHEN gaps.open_here > 0 THEN 'Partial' ELSE 'LegacyUnknown' END, FALSE, FALSE,
           1, contract_version, stamped_at, stamped_at
    FROM unnest(facets) AS facet_name
    CROSS JOIN LATERAL (SELECT workflow_run_capture_gap_open_count(team, run, facet_name::varchar) AS open_here) AS gaps
    ON CONFLICT (team_id, workflow_run_id, facet) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$ LANGUAGE plpgsql;

-- Unchanged from 0166 except for WHICH NULL absorbs. The lock is still the first statement, the gap probes still run
-- under it, the mask latch is untouched and the delta arithmetic is identical for every facet a producer has already
-- declared. What differs is that an expectation nobody has declared yet is established by the first positive delta
-- rather than absorbing it, and stays NULL under a delta that declares nothing.
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
        known_missing_count, verdict, masked_observed, expectation_declared, revision, schema_version, created_at, last_modified_at)
    VALUES (gen_random_uuid(), team, run, facet_name, expected_delta, present_delta, open_here,
            CASE WHEN open_anywhere OR open_here > 0 OR present_delta < expected_delta THEN 'Partial'
                 WHEN masked THEN 'RedactedExact' ELSE 'Exact' END,
            masked, TRUE, 1, contract_version, stamped_at, stamped_at)
    ON CONFLICT (team_id, workflow_run_id, facet) DO UPDATE SET
        -- The expectation stays indeterminate when it was UN-STATED (expectation_declared, absorbing exactly as 0148
        -- made it absorb), and when nobody has declared one and this delta declares none either. Otherwise the first
        -- positive declaration establishes it and every later delta adds to it.
        expected_record_count = CASE WHEN statement.expected_record_count IS NULL AND (statement.expectation_declared OR expected_delta = 0)
                                     THEN NULL ELSE COALESCE(statement.expected_record_count, 0) + expected_delta END,
        present_record_count = statement.present_record_count + present_delta,
        known_missing_count = GREATEST(statement.known_missing_count, excluded.known_missing_count),
        -- The latch. Monotonic: a run whose bytes were ever masked can never read back as verbatim, however many
        -- unmasked batches follow and however many Partial verdicts pass over the row in between.
        masked_observed = statement.masked_observed OR excluded.masked_observed,
        expectation_declared = statement.expectation_declared OR expected_delta > 0,
        verdict = CASE
            WHEN statement.expected_record_count IS NULL AND (statement.expectation_declared OR expected_delta = 0) THEN statement.verdict
            WHEN excluded.verdict = 'Partial' THEN 'Partial'
            WHEN statement.present_record_count + present_delta < COALESCE(statement.expected_record_count, 0) + expected_delta THEN 'Partial'
            WHEN GREATEST(statement.known_missing_count, excluded.known_missing_count) > 0 THEN 'Partial'
            WHEN statement.masked_observed OR excluded.masked_observed THEN 'RedactedExact'
            ELSE 'Exact' END,
        revision = statement.revision + 1,
        last_modified_at = GREATEST(statement.last_modified_at, stamped_at);
END;
$$ LANGUAGE plpgsql;

-- Unchanged from 0148 except that it now LATCHES what it did. Setting expected_record_count to NULL is no longer by
-- itself what makes the un-stating permanent, because an initializer's NULL must still be establishable, so the
-- permanence is written down: expectation_declared TRUE is what the advance above absorbs against.
--
-- Its predicate moves with it. "Already indeterminate" was the right idempotence test while NULL meant one thing; now
-- a facet that was only ever INITIALIZED is indeterminate and not yet un-stated, and a producer that gives up on it
-- must still be able to make that permanent. Only an already-un-stated statement is left alone.
CREATE OR REPLACE FUNCTION workflow_run_data_manifest_unstate_expectation(team UUID, run UUID, facet_name TEXT) RETURNS BIGINT AS $$
DECLARE
    revised BIGINT;
BEGIN
    -- Same reason, same position: the SET list below probes the gap plane, so the probe and the guard's re-probe have
    -- to see one set.
    PERFORM workflow_run_data_completeness_lock(team, run);

    UPDATE workflow_run_data_manifest SET
        expected_record_count = NULL,
        expectation_declared = TRUE,
        known_missing_count = GREATEST(known_missing_count, workflow_run_capture_gap_open_count(team_id, workflow_run_id, facet)),
        verdict = CASE WHEN GREATEST(known_missing_count, workflow_run_capture_gap_open_count(team_id, workflow_run_id, facet)) > 0
                       THEN 'Partial' ELSE 'LegacyUnknown' END,
        revision = revision + 1,
        last_modified_at = GREATEST(last_modified_at, clock_timestamp())
    WHERE team_id = team AND workflow_run_id = run AND facet = facet_name
      AND NOT (expected_record_count IS NULL AND expectation_declared);

    GET DIAGNOSTICS revised = ROW_COUNT;

    RETURN revised;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION workflow_run_data_manifest_initialize(UUID, UUID, TEXT[], INTEGER) IS
    'Idempotently states that each registered producer facet EXISTS and that nobody has established what it should contain: expected_record_count NULL, which ck_workflow_run_data_manifest_completeness refuses every complete verdict over. Takes the per-run rendezvous lock as its first statement; replay never revises an existing statement.';
COMMENT ON FUNCTION workflow_run_data_manifest_advance(UUID, UUID, TEXT, BIGINT, BIGINT, BOOLEAN, INTEGER) IS
    'The only way a producer advances a facet''s completeness statement. Takes the per-run rendezvous lock as its first statement, so the gap probe and the write it feeds cannot be separated by a committing gap. Callers need no transaction and no lock of their own. A masked observation latches in masked_observed, so a run whose bytes were ever masked never reads back as verbatim. An expectation that was un-stated absorbs every later delta; one nobody has declared yet is established by the first positive one.';
COMMENT ON FUNCTION workflow_run_data_manifest_unstate_expectation(UUID, UUID, TEXT) IS
    'Un-states one facet''s expectation (expected_record_count -> NULL, latched in expectation_declared), which 0146 refuses every complete verdict over and the advance above absorbs against. Takes the per-run rendezvous lock as its first statement for the same reason. Returns the number of statements revised; an already un-stated statement is left alone.';
