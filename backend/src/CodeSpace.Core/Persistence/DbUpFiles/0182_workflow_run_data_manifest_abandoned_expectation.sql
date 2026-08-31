-- 0182_workflow_run_data_manifest_abandoned_expectation.sql
--
-- The CONDITIONAL un-stating, for the one caller that does not know what it is writing over.
--
-- workflow_run_data_manifest_unstate_expectation (0148, latched in 0172) is a PRODUCER's verb: the process that gave
-- up on a facet is the same process that would have accounted for it, so it states the abandonment as a fact about
-- itself and nothing may talk it out of that. A maintenance sweep is the opposite case. It never observes
-- abandonment; it INFERS it from a row it read in an earlier transaction, and between that read and this write a
-- producer can commit the accounting that meets the declaration, a gap can land and name the loss exactly, or an
-- operator can continue the terminal run (ContinueFailedRunAsync / ContinueCancelledRunAsync flip Failure and
-- Cancelled back to Pending, so terminality is NOT monotonic). Applying the producer's unconditional verb on that
-- inference destroys whichever of those landed, permanently -- expected_record_count becomes NULL, expectation_declared
-- latches TRUE, and every later delta is absorbed. On a plane whose entire value is that its numbers are trustworthy,
-- that is the worst available failure: the sweep built to remove one false claim manufactures another.
--
-- So the sweep gets its own verb, which re-asks the selecting question INSIDE the write. Every conjunct below is one
-- the caller's SELECT already applied; repeating them here is what makes the pair a compare-and-set rather than a
-- probe-then-write, the same shape the artifact retention reaper's claim/settle pair uses. A row that stopped
-- qualifying is not an error and not a retry -- it is a row whose answer improved, and it is left alone with
-- ROW_COUNT 0, which the caller reports as unchanged.
--
-- THE GAP PLANE IS RE-PROBED, not just read off known_missing_count, for two reasons. The count can lag its floor:
-- workflow_run_capture_gap_mark_manifest reconciles it to workflow_run_capture_gap_open_count only on statements its
-- own predicate matches. And workflow_run_data_manifest_guard refuses any statement whose known_missing_count sits
-- below that floor -- so without the probe, an interleaved gap would not be skipped politely but would RAISE, and the
-- sweep's containment would turn a healthy no-op into a logged failure. Under the rendezvous lock taken as this
-- function's first statement, the probe and the write it feeds cannot be separated by a committing gap.
--
-- THE VERDICT IS NOT RE-CHECKED, though the caller's read carries it. ck_workflow_run_data_manifest_completeness
-- already refuses 'Exact' and 'RedactedExact' over expected_record_count IS NOT NULL AND present_record_count <
-- expected_record_count, so on exactly the rows the counts below select the verdict conjunct is a tautology. It earns
-- its place in the READ only because it is what lets that query reach ix_workflow_run_data_manifest_incomplete; an
-- UPDATE by primary identity has no partial index to reach.
--
-- Rollback: DROP FUNCTION workflow_run_data_manifest_unstate_abandoned_expectation(UUID, UUID, TEXT, TIMESTAMPTZ);
--           The producer verb it sits beside is untouched, so dropping this one only removes the sweep's ability to
--           write at all -- it can never fall back to the unconditional verb, which is the point of the separation.

CREATE OR REPLACE FUNCTION workflow_run_data_manifest_unstate_abandoned_expectation(team UUID, run UUID, facet_name TEXT, settled_before TIMESTAMPTZ) RETURNS BIGINT AS $$
DECLARE
    revised BIGINT;
BEGIN
    -- Same reason and same position as every other write on this plane: the WHERE clause below probes the gap plane,
    -- so the probe and the guard's re-probe have to see one set.
    PERFORM workflow_run_data_completeness_lock(team, run);

    UPDATE workflow_run_data_manifest SET
        expected_record_count = NULL,
        expectation_declared = TRUE,
        verdict = 'LegacyUnknown',
        revision = revision + 1,
        last_modified_at = GREATEST(last_modified_at, clock_timestamp())
    WHERE team_id = team AND workflow_run_id = run AND facet = facet_name
      AND expected_record_count IS NOT NULL
      AND present_record_count < expected_record_count
      AND known_missing_count = 0
      AND workflow_run_capture_gap_open_count(team_id, workflow_run_id, facet) = 0
      AND last_modified_at <= settled_before
      AND EXISTS (SELECT 1 FROM workflow_run WHERE workflow_run.id = run AND workflow_run.team_id = team
                    AND workflow_run.status IN ('Success', 'Failure', 'Cancelled'));

    GET DIAGNOSTICS revised = ROW_COUNT;

    RETURN revised;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION workflow_run_data_manifest_unstate_abandoned_expectation(UUID, UUID, TEXT, TIMESTAMPTZ) IS
    'The maintenance sweep''s un-stating: expected_record_count -> NULL, latched in expectation_declared, but ONLY on a facet that STILL reads as the unattributed shortfall of a terminal run nothing has advanced since settled_before. Takes the per-run rendezvous lock as its first statement, and re-probes the open-gap plane under it. Returns the number of statements revised; 0 means the row stopped qualifying between the caller''s read and this write, which is an answer that improved rather than a failure. A producer stating abandonment about its OWN facet uses workflow_run_data_manifest_unstate_expectation instead, which asks nothing.';
