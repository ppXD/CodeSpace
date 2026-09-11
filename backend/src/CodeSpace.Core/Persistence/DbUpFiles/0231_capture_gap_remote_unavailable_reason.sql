-- Lets a capture gap say the remote was UNAVAILABLE, which none of the four existing reasons can say honestly.
--
-- 0230 gives a stream the vocabulary to hold its bytes through a transient outage. That wait has a ceiling, and past
-- it the bytes the sandbox spool is still holding will never be captured by this session -- a real loss, which has to
-- be named or the completeness plane reports data it does not have.
--
-- None of the existing four fits:
--
--   * BoundExceeded says a CONFIGURED bound stopped the capture and the bytes past the cut were never taken from the
--     source. Here a bound did end the wait, but the source still has them; the reason an operator needs is the
--     outage, not the timer that gave up on it.
--   * WriteRefused says the frame reached capture and did not reach storage -- the refusal was a VERDICT. A transient
--     503 is the opposite: storage never answered, and the same write would have been accepted minutes earlier or
--     later. Folding the two together is what would make an incident indistinguishable from a quota or an admission
--     failure in the one column an operator reads to triage.
--   * ReattachTorn and FrameUnreadable are about capture's own position and its own bytes, neither of which moved.
--
-- ONLY the gap vocabulary widens. No facet is added to workflow_run_data_manifest: a facet needs a declarable expected
-- count, and nothing knows in advance how many segments an outage will cost.
ALTER TABLE workflow_run_capture_gap DROP CONSTRAINT ck_workflow_run_capture_gap_reason;
ALTER TABLE workflow_run_capture_gap ADD CONSTRAINT ck_workflow_run_capture_gap_reason CHECK (
    reason IN ('BoundExceeded', 'WriteRefused', 'ReattachTorn', 'FrameUnreadable', 'RemoteUnavailable')
    AND (reason_detail IS NULL OR btrim(reason_detail) <> ''));

COMMENT ON CONSTRAINT ck_workflow_run_capture_gap_reason ON workflow_run_capture_gap IS
    'Why a span is missing, closed on purpose and still with no Unknown arm. RemoteUnavailable covers a transient storage outage that outlived the producer''s backpressure ceiling: storage never answered, so the span is neither a verdict against the write nor a bound that stopped reading.';
