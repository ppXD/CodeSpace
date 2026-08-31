-- 0181_workflow_run_produced_file_owner_kinds.sql
--
-- Admits the two nouns for a file the RUN produced -- 'node-output' and 'deliverable' -- to the three constraints that
-- decide what the completeness plane can talk about: a gap's subject, the row that cites what RECOVERED a gap, and a
-- manifest facet. Until now a run's own output was the one thing this plane could name a loss in (0175 widened the
-- subject alone) but could never state completeness over, and 'deliverable' -- the publish path's captured file, which
-- is the run's ANSWER rather than a byproduct of computing it -- could not be named at all.
--
-- THE REASONING THIS OVERTURNS, quoted because it is written into 0175 and a reader will find it there: a facet
-- nothing ever advances "would sit at expected=0 forever and read as complete -- exactly the false claim this
-- migration exists to prevent". That was already untrue when it was written, and 0172 is why. It replaced 0171's
-- determinate zero: workflow_run_data_manifest_initialize now mints expected_record_count NULL, verdict
-- 'LegacyUnknown', expectation_declared FALSE, and ck_workflow_run_data_manifest_completeness refuses every complete
-- verdict over a NULL expectation. So an unadvanced facet reads INDETERMINATE, never complete. The stronger half is
-- that neither noun is in RunDataManifestCoverage.RequiredFacets, so the initializer mints no row for them at all --
-- and no row is the same indeterminate answer, reached without any statement existing.
--
-- WHAT THIS DOES NOT DO. Admitting a noun reserves a WORD. It advances nothing, mints nothing, and adds no producer:
-- until one declares an expectation, a run's manifest looks exactly as it does today. What it buys is that the day a
-- producer wants to state "this run undertook to capture three deliverables", the database has somewhere to put it
-- instead of the statement being refused and the shortfall going unrecorded.
--
-- The three lists are kept spelled IDENTICALLY to their EF mirrors in WorkflowRunCaptureGapConfiguration and
-- WorkflowRunDataManifestConfiguration; WorkflowRunDataCompletenessSchemaTests compares this file, as the last
-- migration to state each constraint, against the model.
--
-- Rollback: restate all three constraints from 0175 (subject) and 0151 (resolution, facet). No row can violate the
-- narrower lists unless a producer has meanwhile written one under a new noun.

ALTER TABLE workflow_run_capture_gap DROP CONSTRAINT ck_workflow_run_capture_gap_subject;
ALTER TABLE workflow_run_capture_gap ADD CONSTRAINT ck_workflow_run_capture_gap_subject CHECK (
    subject_kind IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'node-output', 'deliverable', 'capture-gap', 'data-manifest')
    AND (subject_id IS NULL OR btrim(subject_id) <> '') AND btrim(capture_source) <> '');

ALTER TABLE workflow_run_capture_gap DROP CONSTRAINT ck_workflow_run_capture_gap_resolution;
ALTER TABLE workflow_run_capture_gap ADD CONSTRAINT ck_workflow_run_capture_gap_resolution CHECK (
    (resolution = 'Open' AND recovered_at IS NULL AND recovered_by_kind IS NULL AND recovered_by_id IS NULL)
    OR (resolution = 'Recovered' AND recovered_at IS NOT NULL AND recovered_at >= noticed_at
        AND recovered_by_kind IS NOT NULL AND recovered_by_kind IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'node-output', 'deliverable', 'capture-gap', 'data-manifest')
        AND recovered_by_id IS NOT NULL AND btrim(recovered_by_id) <> ''));

ALTER TABLE workflow_run_data_manifest DROP CONSTRAINT ck_workflow_run_data_manifest_facet;
ALTER TABLE workflow_run_data_manifest ADD CONSTRAINT ck_workflow_run_data_manifest_facet CHECK (
    facet IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'node-output', 'deliverable', 'capture-gap', 'data-manifest'));

COMMENT ON CONSTRAINT ck_workflow_run_capture_gap_subject ON workflow_run_capture_gap IS
    'The subjects a gap may be about. node-output covers bytes a node produced that a storage failure kept out of their destination; deliverable covers a file the run was asked to produce. Both settle the run, and this row is what stops it also claiming complete data.';
COMMENT ON CONSTRAINT ck_workflow_run_data_manifest_facet ON workflow_run_data_manifest IS
    'The facets a completeness statement may be about, one per registered owner noun. A noun with no producer has no row, which is the indeterminate answer; nothing here mints one.';
