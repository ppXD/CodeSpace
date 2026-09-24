-- 0238_workflow_run_wait_discarded_status.sql
--
-- A run's terminal teardown (an operator cancel, or the engine landing the run Failure/Cancelled) closed its
-- still-pending waits as 'Resolved' — the status a real answer writes — while payload_jsonb still held the node's
-- REQUEST. Continue then replayed that request into the node as its answer: a capped agent.run read its own task and
-- failed "cumulative spend is missing" on every later Continue, an approval read approved=false with no approver.
-- The teardown now closes them as 'Discarded', which no replay reader treats as an answer. Widen the status CHECK to
-- admit it ('Discarded' is 9 chars, inside the existing VARCHAR(16)). Idempotent (DROP IF EXISTS + re-ADD), mirroring
-- 0094's wait_kind widening.
--
-- No backfill: a 'Resolved' row's payload holds EITHER a request (teardown-closed) or an answer (overwritten on
-- resolve), and no column records which, so existing rows cannot be told apart safely.

ALTER TABLE workflow_run_wait DROP CONSTRAINT IF EXISTS workflow_run_wait_status_check;

ALTER TABLE workflow_run_wait
    ADD CONSTRAINT workflow_run_wait_status_check
    CHECK (status IN ('Pending', 'Resolved', 'Discarded'));
