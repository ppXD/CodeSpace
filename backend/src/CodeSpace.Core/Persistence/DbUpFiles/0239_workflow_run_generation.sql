-- 0239_workflow_run_generation.sql
--
-- A Stop reaches only a walk running on the host that took the request, and its teardown (kill-wave, discarding the
-- run's pending waits) runs after the cancel commits. A Continue landing in between revived the run while both were
-- still in flight: the old walk, on another host, saw Running (the new walk's claim) at its next wave check and kept
-- walking, so a step could run twice; and the late teardown discarded the Continue's fresh waits and cancelled its new
-- agents.
--
-- generation numbers the run's revivals. Continue bumps it in the same statement that revives the run; a walk claims
-- the generation it read, and its wave checks and terminal writes compare against it; the cancel's teardown carries
-- the generation it cancelled and does nothing once that has moved. Existing rows start at 0 — no run revived before
-- this column existed has a walk or a teardown still in flight — and a constant default adds the column without a
-- table rewrite.

ALTER TABLE workflow_run ADD COLUMN IF NOT EXISTS generation integer NOT NULL DEFAULT 0;
