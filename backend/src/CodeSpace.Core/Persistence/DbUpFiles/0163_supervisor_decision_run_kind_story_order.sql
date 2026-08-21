-- 0163_supervisor_decision_run_kind_story_order.sql
--
-- Observation leaf readers page one open decision kind at a time. The 0161 (run, story_order) index preserves the
-- global story, but a run with many newer decisions of other kinds still has to walk O(run) entries to fill one kind's
-- bounded page. Keep the global axis and add the kind between its exact run identity and immutable story boundary.
-- This is generic substrate, not a Plan-only partial index: later bounded leaf readers can reuse it without migration
-- churn. Decision execution, replay, Sequence and observation-revision authority are unchanged.

CREATE INDEX IF NOT EXISTS ix_supervisor_decision_run_kind_story_order
    ON supervisor_decision (supervisor_run_id, decision_kind, story_order);

COMMENT ON INDEX ix_supervisor_decision_run_kind_story_order IS
    'Supports exact-run, exact-open-kind immutable story keyset pages without scanning newer rows of other kinds.';
