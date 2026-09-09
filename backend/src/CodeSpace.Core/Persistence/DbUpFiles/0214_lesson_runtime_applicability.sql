-- Generic, provenance-bound applicability selectors. Empty arrays preserve every legacy lesson as runtime-agnostic.
ALTER TABLE lesson ADD COLUMN IF NOT EXISTS applicable_models TEXT[] NOT NULL DEFAULT '{}';
ALTER TABLE lesson ADD COLUMN IF NOT EXISTS applicable_harnesses TEXT[] NOT NULL DEFAULT '{}';
ALTER TABLE lesson ADD COLUMN IF NOT EXISTS required_tools TEXT[] NOT NULL DEFAULT '{}';

ALTER TABLE lesson DROP CONSTRAINT IF EXISTS ck_lesson_applicability_bounds;
ALTER TABLE lesson ADD CONSTRAINT ck_lesson_applicability_bounds CHECK (
    cardinality(applicable_models) <= 20
    AND cardinality(applicable_harnesses) <= 20
    AND cardinality(required_tools) <= 20);

COMMENT ON COLUMN lesson.applicable_models IS 'Normalized model ids observed on every cited source run; empty means model-agnostic.';
COMMENT ON COLUMN lesson.applicable_harnesses IS 'Normalized harness kinds observed on every cited source run; empty means harness-agnostic.';
COMMENT ON COLUMN lesson.required_tools IS 'Normalized tool capabilities observed on every cited source run and required at retrieval; empty means no tool precondition.';
