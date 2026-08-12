-- 0113_completion_assessment_metric_projection.sql
--
-- P0-A (dual projection): the metric@1 columns beside the operational assessment. Every compose now emits two
-- isolated projections from the same facts and admission rules — the operational assessment (terminal authority's
-- input, existing columns) and the metric@1 projection (receipts admitted against the FIRST authorized attempt per
-- unit; the solve-rate's only verdict). metric_outcome is the queryable name; metric_jsonb carries the full
-- self-describing projection (@1 attempt refs, obligation set, frozen statistical unit "run@1", policy/suite
-- versions). NULL = a pre-projection row: the run reads unassessed — never solved — until its next compose.
-- Rollback: ALTER TABLE completion_assessment DROP COLUMN metric_outcome, DROP COLUMN metric_jsonb. Idempotent.

ALTER TABLE completion_assessment ADD COLUMN IF NOT EXISTS metric_outcome VARCHAR(40);
ALTER TABLE completion_assessment ADD COLUMN IF NOT EXISTS metric_jsonb JSONB;
