-- Lets a capture gap be ABOUT a node's output.
--
-- Two producers swallow a storage failure and settle anyway, both for a defensible reason, and neither leaves a trace
-- a reader can find:
--
--   * WorkflowEngine.CompleteNodeWithOffloadAsync catches a refused destination and settles the node with its FULL
--     outputs inline. Settling is mandatory -- the node's side effect already fired, so re-firing it would be worse
--     than a large row -- but the run then reports success while the bytes that were supposed to be in the operator's
--     bucket are in this database instead.
--   * AgentRunCommandNode drops the untruncated command output entirely, keeping only the capped preview. That was the
--     only copy.
--
-- The fix is not to change either decision. It is to make the loss ATTRIBUTED: a gap row means the completeness plane
-- can no longer call the run's data complete, which is the difference between a green run that is trustworthy and one
-- that merely looks it.
--
-- ONLY the gap vocabulary widens. workflow_run_data_manifest.facet is deliberately left alone: a facet needs a
-- declarable expected COUNT, and nothing knows in advance how many outputs a run will offload. A facet nothing ever
-- advances would sit at expected=0 forever and read as complete -- exactly the false claim this migration exists to
-- prevent.
ALTER TABLE workflow_run_capture_gap DROP CONSTRAINT ck_workflow_run_capture_gap_subject;
ALTER TABLE workflow_run_capture_gap ADD CONSTRAINT ck_workflow_run_capture_gap_subject CHECK (
    subject_kind IN ('model-call', 'model-call-attempt', 'model-call-body-capture', 'harness-execution', 'harness-process-attempt', 'harness-descriptor', 'harness-reduction-checkpoint', 'runner-handle', 'native-record', 'semantic-event', 'tool-call', 'tool-call-attempt', 'log-stream', 'log-segment', 'session', 'session-state-revision', 'capture-gap', 'data-manifest', 'node-output')
    AND (subject_id IS NULL OR btrim(subject_id) <> '') AND btrim(capture_source) <> '');

COMMENT ON CONSTRAINT ck_workflow_run_capture_gap_subject ON workflow_run_capture_gap IS
    'The subjects a gap may be about. node-output covers bytes a node produced that a storage failure kept out of their destination: the run settles, and this row is what stops it also claiming complete data.';
