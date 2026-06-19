-- 0060_workflow_run_wait_decision_kind.sql
--
-- D1 of the durable Decision substrate: a generic, policy-gated, typed DECISION any node can park on (the
-- structured sibling of 'Approval', which is the binary special case). A flow.decision node raises a
-- DecisionRequest and suspends on a 'Decision' wait until answered (human / policy / supervisor / bounded-wait
-- default). Widen the wait_kind CHECK to admit the new kind (mirrors 0034/0035/0041/0054/0055). 'Decision' is
-- 8 chars — fits the existing VARCHAR(24). Idempotent (the CHECK is DROP IF EXISTS + re-ADD).

ALTER TABLE workflow_run_wait DROP CONSTRAINT IF EXISTS workflow_run_wait_wait_kind_check;

ALTER TABLE workflow_run_wait
    ADD CONSTRAINT workflow_run_wait_wait_kind_check
    CHECK (wait_kind IN ('Timer', 'Approval', 'Callback', 'Subworkflow', 'Action', 'AgentRun', 'SupervisorDecision', 'SupervisorAgentWaits', 'Decision'));
