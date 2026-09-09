-- Failure-derived lessons begin as experimental candidates. Only server-observed exact prompt exposure joined to
-- a genuine launch's north-star score can qualify them. Legacy rows remain candidates: no success is invented.
ALTER TABLE lesson ADD COLUMN IF NOT EXISTS successful_exposure_run_ids UUID[] NOT NULL DEFAULT '{}';
ALTER TABLE lesson ADD COLUMN IF NOT EXISTS negative_exposure_run_ids UUID[] NOT NULL DEFAULT '{}';
ALTER TABLE lesson ADD COLUMN IF NOT EXISTS qualified_at TIMESTAMPTZ NULL;
ALTER TABLE lesson ADD COLUMN IF NOT EXISTS qualification_checked_at TIMESTAMPTZ NULL;
ALTER TABLE lesson ADD COLUMN IF NOT EXISTS qualification_suppressed_at TIMESTAMPTZ NULL;

ALTER TABLE lesson DROP CONSTRAINT IF EXISTS ck_lesson_qualification_evidence;
ALTER TABLE lesson ADD CONSTRAINT ck_lesson_qualification_evidence CHECK (
    NOT successful_exposure_run_ids && negative_exposure_run_ids
    AND (qualified_at IS NULL OR (
        cardinality(successful_exposure_run_ids) >= 2
        AND cardinality(successful_exposure_run_ids) > cardinality(negative_exposure_run_ids)))
    AND (qualification_suppressed_at IS NULL OR (
        cardinality(negative_exposure_run_ids) >= 2
        AND cardinality(negative_exposure_run_ids) >= cardinality(successful_exposure_run_ids)))
    AND NOT (qualified_at IS NOT NULL AND qualification_suppressed_at IS NOT NULL));

COMMENT ON COLUMN lesson.successful_exposure_run_ids IS 'Exact genuine-launch exposure receipts whose current scorecard reached unattended solved with delivery.';
COMMENT ON COLUMN lesson.negative_exposure_run_ids IS 'Exact genuine-launch exposure receipts whose current scorecard did not reach unattended solved with delivery.';
COMMENT ON COLUMN lesson.qualified_at IS 'Server-owned formal-rule qualification stamp; null is an experimental candidate. Legacy rows stay null.';
COMMENT ON COLUMN lesson.qualification_checked_at IS 'Operational fairness cursor for bounded qualification sweeps; never interpreted as quality evidence.';
COMMENT ON COLUMN lesson.qualification_suppressed_at IS 'Reversible evidence-based prompt suppression; separate from semantic invalidation.';
