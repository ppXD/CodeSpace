-- One immutable treatment receipt per workflow run. This is the concurrency boundary for arbitrary graphs:
-- concurrent first-wave agent dispatches may propose from the same live lesson window, but the workflow-run primary
-- key selects one winner and every contender reads that exact arm + lesson-id set before persisting its AgentTask.
CREATE TABLE workflow_run_lesson_assignment (
    workflow_run_id UUID PRIMARY KEY,
    team_id UUID NOT NULL,
    arm VARCHAR(16) NOT NULL,
    lesson_ids UUID[] NOT NULL DEFAULT '{}',
    assigned_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT fk_workflow_run_lesson_assignment_run FOREIGN KEY (team_id, workflow_run_id)
        REFERENCES workflow_run(team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_workflow_run_lesson_assignment_arm CHECK (arm IN ('injected', 'withheld', 'none')),
    CONSTRAINT ck_workflow_run_lesson_assignment_receipt CHECK (arm = 'injected' OR cardinality(lesson_ids) = 0)
);

CREATE INDEX ix_workflow_run_lesson_assignment_team_assigned
    ON workflow_run_lesson_assignment(team_id, assigned_at, workflow_run_id);

CREATE FUNCTION protect_workflow_run_lesson_assignment() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'workflow run lesson assignment is immutable';
END;
$$;

CREATE TRIGGER workflow_run_lesson_assignment_immutable
    BEFORE UPDATE OR DELETE ON workflow_run_lesson_assignment
    FOR EACH ROW EXECUTE FUNCTION protect_workflow_run_lesson_assignment();

COMMENT ON TABLE workflow_run_lesson_assignment IS
    'Immutable run-wide cross-run-learning arm and exact lesson exposure receipt, atomically shared by all agent dispatches.';
