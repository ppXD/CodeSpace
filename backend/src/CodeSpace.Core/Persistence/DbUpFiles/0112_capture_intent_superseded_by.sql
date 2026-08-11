-- 0112_capture_intent_superseded_by.sql
--
-- P2 (durable capture, slice 2): an INDETERMINATE capture promise must be formally SUPERSEDABLE by a later
-- CONFIRMED observation of the same run — a re-attach at a bumped epoch that ran the capture to its persist
-- resolves what the dead attempt left unknown. Without the pointer, the run's capture state reads "permanently
-- unknown" forever even after a confirmed capture landed; with it, an audit reads Indeterminate-superseded as
-- resolved-by(id) while an UNsuperseded Indeterminate stays a visible unknown. The supersede is a pointer, never
-- a rewrite: the Indeterminate row keeps its status and history.
--
-- Rollback: ALTER TABLE capture_intent DROP COLUMN superseded_by_intent_id. Idempotent (IF NOT EXISTS).

ALTER TABLE capture_intent ADD COLUMN IF NOT EXISTS superseded_by_intent_id UUID NULL;
