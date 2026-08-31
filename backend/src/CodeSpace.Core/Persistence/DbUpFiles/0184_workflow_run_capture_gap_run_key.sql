-- 0184_workflow_run_capture_gap_run_key.sql
--
-- The gap plane could not represent a gap noticed by a STANDALONE Agent Run, and its producer answered the only way a
-- NOT NULL key leaves open: by recording nothing. That is precisely the silence the table exists to break — an
-- invisible gap is indistinguishable from no gap — so the plane was at its least honest exactly where a run has no
-- workflow run to fall back on.
--
-- The key therefore moves to the run that OWNS the record. workflow_run_id becomes nullable, agent_run_id becomes an
-- owner identity a gap may carry on its own, and ck_workflow_run_capture_gap_owner keeps "every gap names a run" a
-- rule the database enforces rather than a sentence in a doc-comment. This is the shape 0137 already gave
-- workflow_run_harness_execution (agent-run keyed, nullable workflow run) and 0111 gave capture_intent.
--
-- A GAP THAT NAMES AN AGENT RUN NAMES THAT RUN'S PARENT TOO, and the guard derives it rather than trusting a writer
-- to. Every consequence a gap has is reached through workflow_run_id — 0146's downgrade of a complete verdict, its
-- open-gap floor, the run-scoped operator read — so an owner-only gap of a WORKFLOW-BOUND run would sit in the table
-- looking recorded while its run went on reading complete. That is the false-complete this plane exists to prevent,
-- reached through the door nullability opens. A convention would not hold it: the whole reason the workflow key could
-- go null is that one producer could not supply it, and the next producer that cannot is not obliged to notice why.
-- The guard therefore reads the parent off the Agent Run the gap names, exactly as 0137 makes
-- workflow_run_harness_execution mirror the same value, FILLING IN an omitted parent and REFUSING a disagreeing one.
--
-- BOTH KEYS STAY COMPOSITE WITH THE TEAM. fk_..._run already proved (team_id, workflow_run_id); the new
-- fk_..._agent_run proves (team_id, agent_run_id) against ak_agent_run_team_id from 0129. Without it a gap keyed only
-- by its Agent Run would be one nobody proved belongs to the team whose operator summary reads it — the attempt quad's
-- guard used to be that proof, and the split below deliberately removes it from the gaps that need it most.
--
-- THE SPLIT. agent_run_id leaves the all-or-none attempt quad and the remaining TRIPLE stays all-or-none, still
-- requiring an owner. The quad forced a gap to surrender its Agent Run whenever it could not carry
-- harness_process_attempt_id — and the one gap that cannot carry it is the gap whose SUBJECT is a refused attempt
-- insert, since that column hard-references the very write that was refused. The most important gap on this plane was
-- therefore the one that could name no run.
--
-- The guard's execution join moves to IS NOT DISTINCT FROM: a standalone execution carries workflow_run_id NULL, and
-- `NULL = NULL` is NULL, so an equality would have refused every attributed gap of exactly the runs this migration
-- exists to admit. The rendezvous is taken only where there is something to rendezvous WITH — the manifest is still
-- keyed to a workflow run, so a standalone gap has no statement to race — rather than passed a NULL that
-- pg_advisory_xact_lock silently answers with no lock at all.
--
-- Rollback: DROP the owner check and fk_workflow_run_capture_gap_agent_run, restore the quad spelling of
--           ck_workflow_run_capture_gap_attempt_attribution, restore workflow_run_capture_gap_guard from 0155, and set
--           workflow_run_id NOT NULL again — which is possible only after deleting every gap that names no workflow
--           run, and this table refuses DELETE.

ALTER TABLE workflow_run_capture_gap
    ALTER COLUMN workflow_run_id DROP NOT NULL,
    ADD CONSTRAINT ck_workflow_run_capture_gap_owner CHECK (
        workflow_run_id IS NOT NULL OR agent_run_id IS NOT NULL),
    ADD CONSTRAINT fk_workflow_run_capture_gap_agent_run FOREIGN KEY (team_id, agent_run_id)
        REFERENCES agent_run (team_id, id) ON DELETE RESTRICT;

ALTER TABLE workflow_run_capture_gap DROP CONSTRAINT ck_workflow_run_capture_gap_attempt_attribution;
ALTER TABLE workflow_run_capture_gap ADD CONSTRAINT ck_workflow_run_capture_gap_attempt_attribution CHECK (
    (harness_execution_id IS NULL AND harness_process_attempt_id IS NULL AND attempt_worker_fence_epoch IS NULL)
    OR (agent_run_id IS NOT NULL AND harness_execution_id IS NOT NULL AND harness_process_attempt_id IS NOT NULL
        AND attempt_worker_fence_epoch IS NOT NULL AND attempt_worker_fence_epoch > 0));

CREATE OR REPLACE FUNCTION workflow_run_capture_gap_guard() RETURNS trigger AS $$
DECLARE
    exact_attempt_id UUID;
    owner_workflow_run_id UUID;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'workflow_run_capture_gap is never unnoticed — DELETE rejected (id=%). A removable gap makes a complete manifest reachable by deleting the evidence; recovery is a resolution fill, never a delete.', OLD.id;
    END IF;

    IF TG_OP = 'INSERT' THEN
        -- THE OWNER FIRST, because every statement below keys off the workflow run this settles. See the header: a gap
        -- naming an Agent Run must name that run's parent, and the parent is DERIVED here rather than trusted to a
        -- writer.
        IF NEW.agent_run_id IS NOT NULL THEN
            SELECT workflow_run_id INTO owner_workflow_run_id FROM agent_run
            WHERE team_id = NEW.team_id AND id = NEW.agent_run_id
            FOR SHARE;

            IF NOT FOUND THEN
                RAISE EXCEPTION 'workflow_run_capture_gap requires its tenant-bound AgentRun (agent_run=%, team=%).', NEW.agent_run_id, NEW.team_id;
            END IF;

            -- An omitted parent is FILLED IN, a disagreeing one is REFUSED. Refusing the omission would be the wrong
            -- trade on this table alone: a gap write is contained and best-effort, so a refusal turns a recorded loss
            -- back into the silence the plane exists to break. A parent that disagrees is a different claim — a
            -- producer that believes this gap belongs to another run — and silently correcting it would hide that.
            IF NEW.workflow_run_id IS NULL THEN
                NEW.workflow_run_id := owner_workflow_run_id;
            ELSIF NEW.workflow_run_id IS DISTINCT FROM owner_workflow_run_id THEN
                RAISE EXCEPTION 'workflow_run_capture_gap must name its AgentRun''s workflow run exactly (agent_run=%, attempted=%, actual=%).', NEW.agent_run_id, NEW.workflow_run_id, owner_workflow_run_id;
            END IF;
        END IF;

        -- Only a workflow-bound gap has a manifest to race. Passing a NULL run to the lock would not be a smaller
        -- rendezvous, it would be NO rendezvous reported as one: pg_advisory_xact_lock is strict, so it returns NULL
        -- and takes nothing.
        IF NEW.workflow_run_id IS NOT NULL THEN
            PERFORM workflow_run_data_completeness_lock(NEW.team_id, NEW.workflow_run_id);
        END IF;

        IF NEW.resolution <> 'Open' THEN
            RAISE EXCEPTION 'workflow_run_capture_gap must be born Open (id=%, resolution=%). A span that was never visible as missing is indistinguishable from one that was never missed.', NEW.id, NEW.resolution;
        END IF;

        IF NEW.harness_execution_id IS NULL AND NEW.harness_process_attempt_id IS NULL
           AND NEW.attempt_worker_fence_epoch IS NULL THEN
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
          AND execution.workflow_run_id IS NOT DISTINCT FROM NEW.workflow_run_id
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

COMMENT ON COLUMN workflow_run_capture_gap.workflow_run_id IS
    'Workflow run that owns this gap, or NULL for one noticed by a standalone Agent Run; ck_..._owner keeps at least one of the two run keys present.';
COMMENT ON COLUMN workflow_run_capture_gap.agent_run_id IS
    'Agent Run that owns this gap. Independent of the attempt triple, so a gap whose subject is a refused attempt insert can still name the run it belongs to.';
