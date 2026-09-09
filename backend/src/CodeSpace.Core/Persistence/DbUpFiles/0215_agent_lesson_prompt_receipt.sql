-- One immutable exact lesson receipt per logical agent prompt and normalized runtime inside a workflow run.
CREATE TABLE agent_lesson_prompt_receipt (
    workflow_run_id UUID NOT NULL,
    team_id UUID NOT NULL,
    prompt_key CHAR(64) NOT NULL,
    lesson_ids UUID[] NOT NULL DEFAULT '{}',
    selected_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (workflow_run_id, prompt_key),
    CONSTRAINT fk_agent_lesson_prompt_receipt_run FOREIGN KEY (team_id, workflow_run_id)
        REFERENCES workflow_run(team_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_agent_lesson_prompt_receipt_key CHECK (prompt_key ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_agent_lesson_prompt_receipt_bounds CHECK (cardinality(lesson_ids) <= 10)
);

CREATE INDEX ix_agent_lesson_prompt_receipt_team_selected
    ON agent_lesson_prompt_receipt(team_id, selected_at, workflow_run_id, prompt_key);

CREATE FUNCTION protect_agent_lesson_prompt_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'agent lesson prompt receipt is immutable';
END;
$$;

CREATE TRIGGER agent_lesson_prompt_receipt_immutable
    BEFORE UPDATE OR DELETE ON agent_lesson_prompt_receipt
    FOR EACH ROW EXECUTE FUNCTION protect_agent_lesson_prompt_receipt();

COMMENT ON TABLE agent_lesson_prompt_receipt IS
    'Immutable per-logical-agent, per-runtime exact lesson receipt. The workflow assignment owns only the shared experiment arm and inherited upstream receipt.';

COMMENT ON TABLE workflow_run_lesson_assignment IS
    'Immutable run-wide cross-run-learning arm plus any inherited planner/supervisor receipt; agent-specific exposures live in agent_lesson_prompt_receipt and AgentTask.';
