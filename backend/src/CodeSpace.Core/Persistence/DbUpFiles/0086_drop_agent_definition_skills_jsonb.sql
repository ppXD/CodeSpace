-- 0086_drop_agent_definition_skills_jsonb.sql
--
-- Contract step of the skills expand-contract: drop the dormant agent_definition.skills_jsonb blob (added in
-- 0042). It was write-only-dead — the import path only ever wrote the "[]" default and nothing read it; an
-- agent's skills are now relational, via the agent_skill_binding join (0080) resolved by AgentDefinitionResolver
-- at run and surfaced as AgentDefinitionSummary.BoundSkills. With the last writer removed, the column carries no
-- data worth keeping, so this drop is non-destructive.
--
-- Idempotent (IF EXISTS); the matching entity property + EF mapping are removed in the same change.

ALTER TABLE agent_definition DROP COLUMN IF EXISTS skills_jsonb;
