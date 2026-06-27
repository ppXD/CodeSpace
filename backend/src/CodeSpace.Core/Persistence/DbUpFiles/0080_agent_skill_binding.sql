-- 0080_agent_skill_binding.sql
--
-- The many-to-many binding between an Agent persona and the Skills it carries — the relational replacement
-- for the dormant agent_definition.skills_jsonb blob (which still carries imported skills until the importer
-- slice cuts over to this join and then drops the column; expand-contract, so this migration is purely
-- additive). A row exists iff the agent is bound to the skill; unbinding deletes the row.
--
-- Indexed both ways: the per-agent skill list (PK leads with agent) and the reverse "which agents use this
-- skill" (idx on skill). The unique (agent, skill) pair stops a double-bind. Both FKs target their definition
-- table; those soft-delete, so a binding is never orphaned by a hard delete.
--
-- Additive + non-breaking: a brand-new table, nothing else touched. Idempotent (IF NOT EXISTS).

CREATE TABLE IF NOT EXISTS agent_skill_binding (
    id                      UUID         NOT NULL PRIMARY KEY,
    agent_definition_id     UUID         NOT NULL REFERENCES agent_definition(id),
    skill_definition_id     UUID         NOT NULL REFERENCES skill_definition(id),
    created_date            TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    created_by              UUID         NOT NULL
);

-- A skill binds to an agent at most once. Leads with agent_definition_id, so it also serves the forward
-- "skills an agent carries" lookup — no separate single-column agent index needed (matches model_credential_model).
CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_skill_binding_pair
    ON agent_skill_binding(agent_definition_id, skill_definition_id);

-- Reverse lookup: which agents use a given skill.
CREATE INDEX IF NOT EXISTS idx_agent_skill_binding_skill
    ON agent_skill_binding(skill_definition_id);

COMMENT ON TABLE agent_skill_binding IS
    'Many-to-many binding of an Agent persona to the Skills it carries — the relational replacement for '
    'agent_definition.skills_jsonb. Unique (agent, skill); indexed both ways for the forward (agent->skills) '
    'and reverse (skill->agents) library queries.';
