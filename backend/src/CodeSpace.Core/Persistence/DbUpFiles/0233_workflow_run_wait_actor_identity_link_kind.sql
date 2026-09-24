-- 0233_workflow_run_wait_actor_identity_link_kind.sql
--
-- "Park, don't die" for the act-as-user seam. A node that must act AS a specific person's own provider identity
-- (git.pr_review, git.merge_pull_request, the issue writes) used to FAIL the whole run the moment that person had
-- no linked identity — a click that returned 204 and a run that died a minute later in the background. It now parks
-- on an 'ActorIdentityLink' wait whose DeadlineAt walks a poll ladder (the deadline IS the wake; the node re-runs
-- and continues the moment the identity exists), bounded by a window after which it fails honestly. Widen the
-- wait_kind CHECK to admit the new kind (mirrors 0041/0060/0094). 'ActorIdentityLink' is 17 chars — fits the
-- existing VARCHAR(24). Idempotent (DROP IF EXISTS + re-ADD).

ALTER TABLE workflow_run_wait DROP CONSTRAINT IF EXISTS workflow_run_wait_wait_kind_check;

ALTER TABLE workflow_run_wait
    ADD CONSTRAINT workflow_run_wait_wait_kind_check
    CHECK (wait_kind IN ('Timer', 'Approval', 'Callback', 'Subworkflow', 'Action', 'AgentRun', 'SupervisorDecision', 'SupervisorAgentWaits', 'SupervisorInfraPark', 'Decision', 'ActorIdentityLink'));
