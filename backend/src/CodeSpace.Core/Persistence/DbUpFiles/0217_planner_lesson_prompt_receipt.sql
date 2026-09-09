-- One immutable relevance receipt per distinct planner prompt inside a workflow run.
CREATE TABLE planner_lesson_prompt_receipt (
    workflow_run_id UUID NOT NULL,
    team_id UUID NOT NULL,
    prompt_key CHAR(64) NOT NULL,
    lesson_arm VARCHAR(16) NOT NULL,
    lesson_ids UUID[] NOT NULL DEFAULT '{}',
    candidate_ids UUID[] NOT NULL DEFAULT '{}',
    relevance_status VARCHAR(32) NOT NULL,
    relevance_model VARCHAR(500) NULL,
    relevance_generation VARCHAR(100) NOT NULL,
    assessment_digest CHAR(64) NULL,
    selected_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (workflow_run_id, prompt_key),
    CONSTRAINT fk_planner_lesson_prompt_receipt_run FOREIGN KEY (team_id, workflow_run_id)
        REFERENCES workflow_run(team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_planner_lesson_prompt_receipt_key CHECK (prompt_key ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_planner_lesson_prompt_receipt_arm CHECK (lesson_arm IN ('injected', 'withheld', 'none')),
    CONSTRAINT ck_planner_lesson_prompt_receipt_lesson_bounds CHECK (cardinality(lesson_ids) <= 5),
    CONSTRAINT ck_planner_lesson_prompt_receipt_candidate_bounds CHECK (cardinality(candidate_ids) <= 20),
    CONSTRAINT ck_planner_lesson_prompt_receipt_relevance_status CHECK (relevance_status IN ('selected', 'abstained', 'unavailable', 'failed', 'no-candidates', 'withheld')),
    CONSTRAINT ck_planner_lesson_prompt_receipt_selection_subset CHECK (lesson_ids <@ candidate_ids),
    CONSTRAINT ck_planner_lesson_prompt_receipt_selection_shape CHECK ((relevance_status = 'selected') = (cardinality(lesson_ids) > 0)),
    CONSTRAINT ck_planner_lesson_prompt_receipt_arm_shape CHECK (
        (lesson_arm = 'injected' AND relevance_status IN ('selected', 'abstained', 'unavailable', 'failed')) OR
        (lesson_arm = 'withheld' AND relevance_status = 'withheld') OR
        (lesson_arm = 'none' AND relevance_status = 'no-candidates')),
    CONSTRAINT ck_planner_lesson_prompt_receipt_candidate_shape CHECK ((lesson_arm = 'none') = (cardinality(candidate_ids) = 0)),
    CONSTRAINT ck_planner_lesson_prompt_receipt_assessment_shape CHECK (relevance_status NOT IN ('selected', 'abstained') OR assessment_digest IS NOT NULL),
    CONSTRAINT ck_planner_lesson_prompt_receipt_generation CHECK (relevance_generation <> ''),
    CONSTRAINT ck_planner_lesson_prompt_receipt_assessment_digest CHECK (assessment_digest IS NULL OR assessment_digest ~ '^[0-9a-f]{64}$')
);

CREATE INDEX ix_planner_lesson_prompt_receipt_team_selected
    ON planner_lesson_prompt_receipt(team_id, selected_at, workflow_run_id, prompt_key);

CREATE FUNCTION protect_planner_lesson_prompt_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'planner lesson prompt receipt is immutable';
END;
$$;

CREATE TRIGGER planner_lesson_prompt_receipt_immutable
    BEFORE UPDATE OR DELETE ON planner_lesson_prompt_receipt
    FOR EACH ROW EXECUTE FUNCTION protect_planner_lesson_prompt_receipt();

COMMENT ON TABLE planner_lesson_prompt_receipt IS
    'Immutable per-distinct-planner-prompt semantic lesson receipt. Identical at-least-once retries reuse exact exposure; revised prompt text creates a new receipt.';
