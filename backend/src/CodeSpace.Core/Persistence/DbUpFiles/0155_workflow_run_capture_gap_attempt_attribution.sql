-- 0155_workflow_run_capture_gap_attempt_attribution.sql
--
-- A native-record write refusal can happen on the FIRST batch of a stream. In that case the gap has a stream id but
-- there is deliberately no workflow_run_native_record row to join through, so deriving its Agent Run from a surviving
-- native row silently makes the most important gap unattributable. Capture already holds the exact execution and
-- process-attempt identity before it attempts the frame write. Persist that identity on the gap itself.
--
-- The launch fence is copied as the immutable fence of the process attempt, not read from agent_run later. A valid
-- re-attach raises agent_run.fence_epoch while continuing to observe the same process; comparing to the mutable current
-- fence would reject honest resumed gaps. The insert guard instead proves all four coordinates against the frozen
-- workflow_run_harness_process_attempt row and proves that row's execution belongs to this workflow run.
--
-- Existing and non-harness gaps remain legal under the wholly-null arm. A partial coordinate is refused. The process
-- attempt is append-only and DELETE-refused by 0137, and the FK adds the ordinary referential floor as well.
-- Rollback: drop ix_workflow_run_capture_gap_agent_run_noticed, fk/check constraints and the four added columns, then
-- restore workflow_run_capture_gap_guard from 0146.

ALTER TABLE workflow_run_capture_gap
    ADD COLUMN agent_run_id UUID NULL,
    ADD COLUMN harness_execution_id UUID NULL,
    ADD COLUMN harness_process_attempt_id UUID NULL,
    ADD COLUMN attempt_worker_fence_epoch BIGINT NULL,
    ADD CONSTRAINT ck_workflow_run_capture_gap_attempt_attribution CHECK (
        (agent_run_id IS NULL AND harness_execution_id IS NULL AND harness_process_attempt_id IS NULL
            AND attempt_worker_fence_epoch IS NULL)
        OR (agent_run_id IS NOT NULL AND harness_execution_id IS NOT NULL AND harness_process_attempt_id IS NOT NULL
            AND attempt_worker_fence_epoch IS NOT NULL AND attempt_worker_fence_epoch > 0)),
    ADD CONSTRAINT fk_workflow_run_capture_gap_harness_process_attempt FOREIGN KEY (harness_process_attempt_id)
        REFERENCES workflow_run_harness_process_attempt(id) ON DELETE RESTRICT;

-- The bounded operator read is per Agent Run and newest-first. Partial so legacy and non-harness gaps add no dead
-- index entries, and team leads because every operator read is tenant-scoped before it names the run.
CREATE INDEX ix_workflow_run_capture_gap_agent_run_noticed
    ON workflow_run_capture_gap (team_id, agent_run_id, noticed_at, id)
    WHERE agent_run_id IS NOT NULL;

CREATE OR REPLACE FUNCTION workflow_run_capture_gap_guard() RETURNS trigger AS $$
DECLARE
    exact_attempt_id UUID;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'workflow_run_capture_gap is never unnoticed — DELETE rejected (id=%). A removable gap makes a complete manifest reachable by deleting the evidence; recovery is a resolution fill, never a delete.', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        PERFORM workflow_run_data_completeness_lock(NEW.team_id, NEW.workflow_run_id);

        IF NEW.resolution <> 'Open' THEN
            RAISE EXCEPTION 'workflow_run_capture_gap must be born Open (id=%, resolution=%). A span that was never visible as missing is indistinguishable from one that was never missed.', NEW.id, NEW.resolution;
        END IF;

        IF NEW.agent_run_id IS NULL AND NEW.harness_execution_id IS NULL
           AND NEW.harness_process_attempt_id IS NULL AND NEW.attempt_worker_fence_epoch IS NULL THEN
            RETURN NEW;
        END IF;

        IF NEW.agent_run_id IS NULL OR NEW.harness_execution_id IS NULL
           OR NEW.harness_process_attempt_id IS NULL OR NEW.attempt_worker_fence_epoch IS NULL THEN
            RAISE EXCEPTION 'workflow_run_capture_gap process attribution is all-or-none (id=%). A partial AgentRun/execution/attempt/fence coordinate would make a reader guess.', NEW.id;
        END IF;

        -- The attempt id is globally unique, so this is one indexed PK lookup plus its indexed execution join. Every
        -- repeated coordinate is checked on purpose: this is the admission point that turns caller-held ids into a
        -- durable exact attribution. No native-record row participates, because the refused batch may have none.
        SELECT attempt.id INTO exact_attempt_id
        FROM workflow_run_harness_process_attempt AS attempt
        JOIN workflow_run_harness_execution AS execution
          ON execution.id = attempt.execution_id
         AND execution.team_id = attempt.team_id
         AND execution.agent_run_id = attempt.agent_run_id
        WHERE attempt.id = NEW.harness_process_attempt_id
          AND attempt.team_id = NEW.team_id
          AND attempt.agent_run_id = NEW.agent_run_id
          AND attempt.execution_id = NEW.harness_execution_id
          AND attempt.worker_fence_epoch = NEW.attempt_worker_fence_epoch
          AND execution.workflow_run_id = NEW.workflow_run_id
        FOR SHARE OF attempt, execution;

        IF exact_attempt_id IS NULL THEN
            RAISE EXCEPTION 'workflow_run_capture_gap attribution does not match one frozen harness process attempt (id=%, team=%, workflow_run=%, agent_run=%, execution=%, attempt=%, worker_fence=%).',
                NEW.id, NEW.team_id, NEW.workflow_run_id, NEW.agent_run_id, NEW.harness_execution_id,
                NEW.harness_process_attempt_id, NEW.attempt_worker_fence_epoch;
        END IF;

        RETURN NEW;
    END IF;

    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.team_id IS DISTINCT FROM OLD.team_id
       OR NEW.workflow_run_id IS DISTINCT FROM OLD.workflow_run_id
       OR NEW.agent_run_id IS DISTINCT FROM OLD.agent_run_id
       OR NEW.harness_execution_id IS DISTINCT FROM OLD.harness_execution_id
       OR NEW.harness_process_attempt_id IS DISTINCT FROM OLD.harness_process_attempt_id
       OR NEW.attempt_worker_fence_epoch IS DISTINCT FROM OLD.attempt_worker_fence_epoch
       OR NEW.subject_kind IS DISTINCT FROM OLD.subject_kind OR NEW.subject_id IS DISTINCT FROM OLD.subject_id
       OR NEW.stream_id IS DISTINCT FROM OLD.stream_id OR NEW.channel IS DISTINCT FROM OLD.channel
       OR NEW.range_kind IS DISTINCT FROM OLD.range_kind OR NEW.range_start IS DISTINCT FROM OLD.range_start
       OR NEW.range_end IS DISTINCT FROM OLD.range_end
       OR NEW.range_started_at IS DISTINCT FROM OLD.range_started_at
       OR NEW.range_ended_at IS DISTINCT FROM OLD.range_ended_at
       OR NEW.reason IS DISTINCT FROM OLD.reason OR NEW.reason_detail IS DISTINCT FROM OLD.reason_detail
       OR NEW.capture_source IS DISTINCT FROM OLD.capture_source OR NEW.noticed_at IS DISTINCT FROM OLD.noticed_at
       OR NEW.schema_version IS DISTINCT FROM OLD.schema_version OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'workflow_run_capture_gap is append-only apart from its resolution (id=%). Restating what was missing rewrites a fact a reader already audited; a new observation is a new row.', OLD.id;
    END IF;

    IF OLD.resolution <> 'Open' THEN
        RAISE EXCEPTION 'workflow_run_capture_gap resolution is filled exactly once (id=%, resolution=%).', OLD.id, OLD.resolution;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON COLUMN workflow_run_capture_gap.agent_run_id IS
    'Exact Agent Run whose harness process noticed this gap; all attempt-attribution columns are null or all present.';
COMMENT ON COLUMN workflow_run_capture_gap.harness_execution_id IS
    'Exact durable harness execution owning the process attempt that noticed this gap.';
COMMENT ON COLUMN workflow_run_capture_gap.harness_process_attempt_id IS
    'Exact durable process attempt that noticed this gap; never inferred from a native row the refused batch may not have written.';
COMMENT ON COLUMN workflow_run_capture_gap.attempt_worker_fence_epoch IS
    'Immutable launch fence copied from the attributed process attempt; deliberately not the Agent Run current fence after re-attach.';
