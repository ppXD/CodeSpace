-- Make every immutable prompt receipt explain the bounded candidate set and semantic assessment that produced it.
ALTER TABLE agent_lesson_prompt_receipt
    ADD COLUMN candidate_ids UUID[] NOT NULL DEFAULT '{}',
    ADD COLUMN relevance_status VARCHAR(24) NOT NULL DEFAULT 'legacy',
    ADD COLUMN relevance_model VARCHAR(500),
    ADD COLUMN relevance_generation VARCHAR(100) NOT NULL DEFAULT 'legacy',
    ADD COLUMN assessment_digest CHAR(64),
    ADD CONSTRAINT ck_agent_lesson_prompt_receipt_candidate_bounds CHECK (cardinality(candidate_ids) <= 20),
    ADD CONSTRAINT ck_agent_lesson_prompt_receipt_relevance_status CHECK (relevance_status IN ('selected', 'abstained', 'unavailable', 'failed', 'no-candidates', 'legacy')),
    ADD CONSTRAINT ck_agent_lesson_prompt_receipt_selection_subset CHECK (relevance_status = 'legacy' OR lesson_ids <@ candidate_ids),
    ADD CONSTRAINT ck_agent_lesson_prompt_receipt_selection_shape CHECK ((relevance_status = 'selected' AND cardinality(lesson_ids) > 0) OR (relevance_status <> 'selected' AND cardinality(lesson_ids) = 0) OR relevance_status = 'legacy'),
    ADD CONSTRAINT ck_agent_lesson_prompt_receipt_candidate_shape CHECK (relevance_status = 'legacy' OR (relevance_status = 'no-candidates') = (cardinality(candidate_ids) = 0)),
    ADD CONSTRAINT ck_agent_lesson_prompt_receipt_assessment_shape CHECK (relevance_status NOT IN ('selected', 'abstained') OR assessment_digest IS NOT NULL),
    ADD CONSTRAINT ck_agent_lesson_prompt_receipt_generation CHECK (relevance_generation <> ''),
    ADD CONSTRAINT ck_agent_lesson_prompt_receipt_assessment_digest CHECK (assessment_digest IS NULL OR assessment_digest ~ '^[0-9a-f]{64}$');

COMMENT ON COLUMN agent_lesson_prompt_receipt.candidate_ids IS 'Exact structurally applicable rows shown to the semantic relevance model, before its abstain/select fold.';
COMMENT ON COLUMN agent_lesson_prompt_receipt.relevance_status IS 'selected, explicit abstention, unavailable/failed assessment, no candidates, or a pre-0216 legacy receipt.';
COMMENT ON COLUMN agent_lesson_prompt_receipt.relevance_model IS 'Bounded provider-observed model identity; null means unavailable or unobserved, never a configured alias.';
COMMENT ON COLUMN agent_lesson_prompt_receipt.assessment_digest IS 'SHA-256 of the schema-valid structured relevance response; task and immutable lesson rows retain the inspectable source text.';
